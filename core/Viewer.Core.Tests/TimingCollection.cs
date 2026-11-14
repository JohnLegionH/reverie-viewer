// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Test classes that claim a speed (a frame, a decode, a scan or a deadline measured on the wall clock). They run with no other test
/// class beside them, so the other classes of this suite cannot slow them down; load from outside the test process is not stopped by
/// this, which is why each bound in them is set well above the real cost.
/// </summary>
[CollectionDefinition("TimingTests", DisableParallelization = true)]
public sealed class TimingTestsCollection;
