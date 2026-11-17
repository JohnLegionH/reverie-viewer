// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Shell.Build;

/// <summary>Build › Scripts: what the queue does to every script in the chosen objects.</summary>
public enum ScriptQueueAction { CompileMono, CompileLsl, Reset, SetRunning, SetNotRunning }

/// <summary>Why the queue did not start: no chosen prim has a script, or none that has one may be modified.</summary>
public enum ScriptQueueRefusal { NoScripts, NoPermission }

/// <summary>One object the queue works through: its region handle, id and name.</summary>
public sealed record ScriptQueueObject(uint LocalId, Guid ObjectId, string Name);

/// <summary>One line of the queue's window: a string-table key and its arguments, or (with an empty key) a line as the grid sent it.</summary>
public sealed record ScriptQueueLine(string Key, string Arg = "", string Arg2 = "");

/// <summary>
/// The official script queues (indra/newview/llcompilequeue.cpp LLFloaterCompileQueue, LLFloaterResetQueue, LLFloaterRunQueue and
/// LLFloaterNotRunQueue; read as behaviour, nothing copied): one object at a time its contents are read, and each script in them is
/// recompiled (its text fetched and saved back with the Mono or LSL target, running), reset, or set running or not running. The window
/// shows a line per step, as the official one, and "Done." at the end. Counts of the scripts handled and of those that failed are kept
/// for the window's last line (this viewer's addition).
/// </summary>
public sealed class ScriptQueue
{
    /// <summary>How long one object's contents are waited for (QUEUE_INVENTORY_FETCH_TIMEOUT, 300 seconds).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(300);

    private readonly Guid _agent;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly IScriptTransport? _scripts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly List<ScriptQueueLine> _lines = new();
    private (Guid ObjectId, TaskCompletionSource<IReadOnlyList<TaskItem>> Answer)? _waiting;

    public ScriptQueue(ScriptQueueAction action, IReadOnlyList<ScriptQueueObject> objects, Guid agent, Func<ActionIntent, ActionResult> submit,
        IScriptTransport? scripts, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Action = action;
        Objects = objects;
        _agent = agent;
        _submit = submit;
        _scripts = scripts;
        _delay = delay ?? Task.Delay;
    }

    public ScriptQueueAction Action { get; }
    public IReadOnlyList<ScriptQueueObject> Objects { get; }

    /// <summary>The window's lines so far (a copy; the queue adds lines on its own thread).</summary>
    public IReadOnlyList<ScriptQueueLine> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public bool Done { get; private set; }

    /// <summary>Scripts the action was done to.</summary>
    public int Handled { get; private set; }

    /// <summary>Scripts it failed for (skipped, not fetched, not compiled).</summary>
    public int Failed { get; private set; }

    /// <summary>The window title's string-table key (strings.xml CompileQueueTitle, ResetQueueTitle, RunQueueTitle, NotRunQueueTitle).</summary>
    public static string TitleKey(ScriptQueueAction action) => "queue.title." + Word(action);

    /// <summary>The refusal's string-table key (notifications.xml Cannot&lt;action&gt;SelectObjectsNoScripts and ...NoPermission).</summary>
    public static string RefusalKey(ScriptQueueAction action, ScriptQueueRefusal refusal) =>
        "queue.refused." + Word(action) + (refusal == ScriptQueueRefusal.NoScripts ? ".no_scripts" : ".no_permission");

    /// <summary>A line as the window shows it.</summary>
    public static string Format(ScriptQueueLine line, StringTable strings) => line.Key switch
    {
        "" => line.Arg,
        "queue.starting" => strings.Get(line.Key, strings.Get(line.Arg), line.Arg2),
        _ => strings.Get(line.Key, line.Arg, line.Arg2),
    };

    private static string Word(ScriptQueueAction action) => action switch
    {
        ScriptQueueAction.CompileMono or ScriptQueueAction.CompileLsl => "compile",
        ScriptQueueAction.Reset => "reset",
        ScriptQueueAction.SetRunning => "running",
        _ => "not_running",
    };

    private void Add(string key, string arg = "", string arg2 = "")
    {
        lock (_gate)
            _lines.Add(new ScriptQueueLine(key, arg, arg2));
    }

    /// <summary>A transport event: the contents of the object being worked on.</summary>
    public void Receive(TransportEvent e)
    {
        if (e is not TaskInventoryReceived t)
            return;
        TaskCompletionSource<IReadOnlyList<TaskItem>>? answer = null;
        lock (_gate)
            if (_waiting is { } w && w.ObjectId == t.ObjectId)
                answer = w.Answer;
        answer?.TrySetResult(t.Items);
    }

    /// <summary>Works through the objects one at a time (objectScriptProcessingQueueCoro); returns when done or cancelled.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Add("queue.starting", "queue.start." + Word(Action), Objects.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var obj in Objects)
        {
            if (ct.IsCancellationRequested)
                return;
            var answer = new TaskCompletionSource<IReadOnlyList<TaskItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _waiting = (obj.ObjectId, answer);
            Add("queue.loading", obj.Name);
            _submit(new RequestContentsIntent(obj.LocalId, obj.ObjectId));
            var first = await Task.WhenAny(answer.Task, _delay(Timeout, ct)).ConfigureAwait(false);
            lock (_gate)
                _waiting = null;
            if (first != answer.Task)
            {
                if (ct.IsCancellationRequested)
                    return;
                Add("queue.timeout", obj.Name);
                continue;
            }
            foreach (var item in answer.Task.Result.Where(i => i.AssetType == 10))
            {
                if (ct.IsCancellationRequested)
                    return;
                if (await Process(obj, item, ct).ConfigureAwait(false))
                    Handled++;
                else
                    Failed++;
            }
        }
        Done = true;
        Add("queue.done");
        Add("queue.summary", Handled.ToString(CultureInfo.InvariantCulture), Failed.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>One script: true when the action was done.</summary>
    private async Task<bool> Process(ScriptQueueObject obj, TaskItem item, CancellationToken ct)
    {
        var access = Viewer.Shell.Inventory.ScriptEditing.AccessFor(item, obj.ObjectId, obj.LocalId, _agent)!;
        switch (Action)
        {
            case ScriptQueueAction.Reset:
                // LLFloaterResetQueue::resetObjectScripts: ScriptReset for the item
                Add("queue.resetting", item.Name);
                return _scripts?.ResetScript(access) ?? false;
            case ScriptQueueAction.SetRunning or ScriptQueueAction.SetNotRunning:
                // LLFloaterRunQueue / LLFloaterNotRunQueue: SetScriptRunning for the item
                bool run = Action == ScriptQueueAction.SetRunning;
                Add(run ? "queue.running" : "queue.not_running", item.Name);
                return _scripts?.SetScriptRunning(access, run) ?? false;
        }
        // LLFloaterCompileQueue::processScript: the agent must be able to modify and copy the script
        if (!TaskItemRules.Allows(item, _agent, TaskItemRules.Modify | TaskItemRules.Copy))
        {
            Add("queue.skipping", item.Name);
            return false;
        }
        if (_scripts is null)
        {
            Add("queue.service_unavailable");
            return false;
        }
        var fetched = await _scripts.FetchScriptAsync(access, ct).ConfigureAwait(false);
        if (fetched.Status != AssetFetchStatus.Ok)
        {
            // handleScriptRetrieval: the message for the failure, then the script's name
            Add(fetched.Status switch
            {
                AssetFetchStatus.NotFound => "queue.problem_downloading",
                AssetFetchStatus.Denied => "queue.insufficient_permissions",
                _ => "queue.unknown_failure",
            }, item.Name);
            return false;
        }
        // saved back running, with the chosen target (LLQueuedScriptAssetUpload with is_running true)
        var target = Action == ScriptQueueAction.CompileMono ? ScriptTarget.Mono : ScriptTarget.Lsl2;
        var saved = await _scripts.SaveScriptAsync(access, fetched.Bytes.ToArray(), target, true, ct).ConfigureAwait(false);
        if (saved.Status != ScriptSaveStatus.Saved)
        {
            Add(saved.Status == ScriptSaveStatus.NoService ? "queue.service_unavailable" : "queue.timeout", item.Name);
            return false;
        }
        if (saved.Compiled == true)
        {
            Add("queue.compiled", item.Name);
            return true;
        }
        // the answer's errors, one line each, with their line breaks taken out
        Add("queue.compile_failed", item.Name);
        foreach (var m in saved.Messages)
            Add("", m.Text.Replace("\n", "", StringComparison.Ordinal));
        return false;
    }
}
