// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;

namespace Viewer.Core.Session;

/// <summary>
/// The fields of a login request that change from one attempt to the next. A first attempt carries none of them
/// (terms and critical message not yet agreed, no token, no remembered multifactor hash); a retry after the person has
/// answered a dialog carries what that answer set. The token and the hash are secrets: they are never logged.
/// </summary>
/// <param name="AgreeToTos">The person agreed to the terms of service the grid asked for.</param>
/// <param name="ReadCritical">The person read the grid's critical message.</param>
/// <param name="Token">The one-time code the person typed for a multifactor challenge (null or empty: none).</param>
/// <param name="MfaHash">The multifactor hash the grid sent back after a correct token; sent with the next attempts so the grid does not ask again.</param>
public sealed record LoginExtras(bool AgreeToTos = false, bool ReadCritical = false, Secret? Token = null, Secret? MfaHash = null)
{
    public bool HasToken => Token is not null && Token.Reveal().Length > 0;
}

/// <summary>
/// What the login service said, beyond the one-line failure code: its reason key, its message, the id and arguments of a
/// message the viewer has its own text for, the error code of a critical message, the multifactor hash it handed out, and
/// a certificate it described. Fields the reply did not carry are empty.
/// </summary>
public sealed record LoginReply(
    string Reason, string Message, string MessageId, IReadOnlyDictionary<string, string> MessageArgs, string ErrorCode, Secret? MfaHash, string Certificate)
{
    /// <summary>The login reply's first-login flag (login-flags, ever_logged_in): true when the grid says this account never logged in
    /// before, false when it says it did, null when the reply carries no such flag.</summary>
    public bool? FirstLogin { get; init; }

    public static LoginReply Empty { get; } = new("", "", "", new Dictionary<string, string>(), "", null, "");
}
