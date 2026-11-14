// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Microsoft.Extensions.Logging;
using Viewer.Core.Diagnostics;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Viewer.Core.Protocol;

/// <summary>
/// LibreMetaverse 3.1.6's logging, routed into the core's redacted log. What the assembly exposes (checked
/// by reflection): a static, process-global <c>LibreMetaverse.Logger</c> built on Microsoft.Extensions.Logging,
/// with <c>SetLoggerFactory(ILoggerFactory, string)</c>, an <c>OnLogMessage</c> event, and a default console factory it
/// creates lazily on first use ("Default console logger initialized"), which is what printed the simulator's LAN address.
/// Every message arrives here under one category ("LibreMetaverse") with no session attached (the client appears only as
/// a name prefix in the text), so per-session routing is not possible with 3.1.6: the messages go to one
/// <c>libremetaverse</c> source, as <see cref="EngineText"/> (URL paths, digests, id-shaped values and IP addresses
/// removed). Exceptions keep their type and redacted message, never a stack trace.
/// Installed by <see cref="ProtocolAdapters.CreateLibreMetaverse"/> before any GridClient exists, so the default console
/// factory is never created. Re-installing (each new transport) replaces the factory; no static state of our own.
/// </summary>
internal sealed class LmvLogRoute(CoreLog log) : ILoggerFactory, ILogger
{
    public const string Category = "LibreMetaverse";

    public static void Install(CoreLog log)
    {
        var route = new LmvLogRoute(log.For("libremetaverse"));
        LibreMetaverse.Logger.SetLoggerFactory(route, Category);
        // CoreJ2K (the JPEG 2000 codec LibreMetaverse pins) has its own process-global logger, which otherwise prints
        // "[WARNING]:..." straight to the console; its messages take the same redacted route.
        CoreJ2K.j2k.util.FacilityManager.DefaultMsgLogger = new J2kMsgRoute(route);
    }

    private sealed class J2kMsgRoute(LmvLogRoute route) : CoreJ2K.j2k.util.IMsgLogger
    {
        public void printmsg(int sev, string msg) => route.Log(sev >= CoreJ2K.j2k.util.MsgLogger_Fields.WARNING ? MsLogLevel.Warning : MsLogLevel.Information,
            default, "[CoreJ2K] " + msg, null, (m, _) => m);
        public void println(string str, int flind, int ind) => printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.INFO, str);
        public void flush() { }
    }

    /// <summary>Library warnings that a healthy session repeats all the time: an event-queue poll that came back with no body (about once
    /// a minute for each region), and one line for each parcel-overlay packet. They are kept as debug entries.</summary>
    private static bool IsRoutine(string text) =>
        text.StartsWith("Skipping LLSD parsing; server returned non-LLSD response", StringComparison.Ordinal)
        || text.StartsWith("Parcel overlay with sequence ID", StringComparison.Ordinal);

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }   // never adds a console or any other provider
    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(MsLogLevel logLevel) => logLevel >= MsLogLevel.Information;

    public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        string text;
        try { text = formatter(state, exception); }
        catch { text = "(unformattable library message)"; }
        var fields = new List<LogField> { CoreLog.F("text", new EngineText(text)) };
        if (IsRoutine(text))
        {
            log.Debug("lmv.debug", [.. fields]);
            return;
        }
        if (exception is not null)
            fields.Add(CoreLog.F("error", new EngineText(exception.GetType().Name + ": " + exception.Message)));
        switch (logLevel)
        {
            case MsLogLevel.Information: log.Info("lmv.info", [.. fields]); break;
            case MsLogLevel.Warning: log.Warn("lmv.warning", [.. fields]); break;
            default: log.Warn("lmv.error", [.. fields]); break;   // Error and Critical: the core's Error takes a raw exception, which is never passed on
        }
    }
}
