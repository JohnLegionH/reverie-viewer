// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>Ask for the items inside an object, so the Contents window can list them.</summary>
public sealed record RequestContentsIntent(uint LocalId, Guid ObjectId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.RequestContents;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ObjectId == Guid.Empty ? [new("contents.no_object")] : [];
    internal override TransportCommand? ToCommand() => new RequestTaskInventoryCommand(LocalId, ObjectId);
}

/// <summary>Copy an object's items into a new inventory folder named after the object (and wear them when <paramref name="Wear"/>).</summary>
public sealed record CopyContentsIntent(uint LocalId, Guid ObjectId, string FolderName, bool Wear, IReadOnlyList<Guid> ItemIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.CopyContents;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (LocalId == 0 || ObjectId == Guid.Empty)
            issues.Add(new("contents.no_object"));
        if (ItemIds.Count == 0)
            issues.Add(new("contents.no_items"));
        if (!InventoryNames.Ok(FolderName))
            issues.Add(new("contents.bad_folder_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new CopyTaskContentsCommand(LocalId, ObjectId, InventoryNames.Clean(FolderName), Wear, ItemIds);
}

/// <summary>Buy an object, a copy of it, or its contents, at the price shown (0 is a valid price).</summary>
public sealed record BuyObjectIntent(uint LocalId, byte SaleType, int Price) : ActionIntent
{
    public override ActionKind Kind => ActionKind.BuyObject;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (LocalId == 0)
            issues.Add(new("buy.no_object"));
        if (SaleType is < 1 or > 3)
            issues.Add(new("buy.bad_sale_type"));
        if (Price < 0)
            issues.Add(new("buy.bad_price"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new BuyObjectCommand(LocalId, SaleType, Price);
}

/// <summary>Ask what an object charges, before the Pay window opens.</summary>
public sealed record RequestPayPriceIntent(uint LocalId, Guid ObjectId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.RequestPayPrice;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ObjectId == Guid.Empty ? [new("pay.no_object")] : [];
    internal override TransportCommand? ToCommand() => new RequestPayPriceCommand(LocalId, ObjectId);
}

/// <summary>Pay an object an amount.</summary>
public sealed record PayObjectIntent(Guid ObjectId, int Amount, string ObjectName) : ActionIntent
{
    public override ActionKind Kind => ActionKind.PayObject;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ObjectId == Guid.Empty)
            issues.Add(new("pay.no_object"));
        if (Amount <= 0)
            issues.Add(new("pay.bad_amount"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new PayObjectCommand(ObjectId, Amount, ObjectName ?? "");
}
