// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

public class EnvironmentPreferenceRowTests
{
    [Theory]
    [InlineData("EnvironmentPersistAcrossLogin")]
    [InlineData("SwitchToSharedEnvAfterTeleport")]
    public void The_environment_settings_have_a_row_on_the_Graphics_page_with_a_label(string key)
    {
        var def = PreferenceCatalog.Find(key);
        Assert.NotNull(def);
        Assert.Equal(PrefPage.Graphics, def!.Page);
        Assert.NotEqual(def.LabelKey, StringTable.English().Get(def.LabelKey));
        Assert.Equal(false, Settings.EnvironmentPersistAcrossLogin.Default);   // off as before: the row only lets a person turn it on
    }
}
