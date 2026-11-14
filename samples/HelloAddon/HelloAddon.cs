// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: 0BSD

using Viewer.Addons;

namespace HelloAddon;

/// <summary>
/// A sample add-on: a menu item opens a small window with a button, and the button says hello in local chat. It asks for two
/// permissions only (see add-on.json): "window", to add its window and menu item, and "chat.send", to speak.
/// </summary>
public sealed class HelloAddon : IAddon
{
    private IAddonContext? _context;

    public void Start(IAddonContext context)
    {
        _context = context;
        var window = context.AddWindow("main", "Hello Sample");
        window.Add(new AddonLabel("about") { Text = "Press the button to say hello in local chat." });
        var button = window.Add(new AddonButton("say") { Text = "Say hello" });
        var said = window.Add(new AddonLabel("said"));
        button.Pressed += () =>
        {
            // Say goes through the same checks as the user's own chat; it answers whether the line was sent
            bool sent = context.Say("Hello from an add-on!");
            said.Text = sent ? "Said hello." : "Not sent (are you logged in?).";
        };
        context.AddMenuItem("Hello Sample", window.Open);
    }

    // The viewer removes the window and the menu item itself; nothing else to undo here.
    public void Stop() => _context = null;
}
