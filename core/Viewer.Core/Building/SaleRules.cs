// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Building;

/// <summary>How an object is sold: the protocol's sale types (llsaleinfo.h EForSale).</summary>
public enum SaleKind : byte { NotForSale = 0, Original = 1, Copy = 2, Contents = 3 }

/// <summary>One selected root as the For sale controls read it.</summary>
/// <param name="OwnerMask">The owner's permission bits (the protocol's, as the region sent them).</param>
public readonly record struct SaleNode(SaleKind Type, int Price, uint OwnerMask);

/// <summary>What the For sale controls show and allow for a selection.</summary>
/// <param name="ForSale">The For Sale box is ticked: at least one root is for sale.</param>
/// <param name="Mixed">Some roots are for sale and some are not (the box is shown as undecided).</param>
/// <param name="Price">The price shown: the first root's, or 0 where the roots differ in being for sale.</param>
/// <param name="PriceMixed">The roots' prices differ.</param>
/// <param name="Type">The sale type shown: the first root's, Copy where none is for sale or the prices differ (the safest choice).</param>
public sealed record SaleView(bool ForSale, bool Mixed, int Price, bool PriceMixed, SaleKind Type, bool BoxEnabled, bool PriceEnabled, bool TypeEnabled);

/// <summary>
/// The For sale controls of the Build window's General tab, from the official panel (llpanelpermissions.cpp refresh, lines 563-660 and
/// 735-800, and setAllSaleInfo, lines 1244-1290; llselectmgr.cpp selectGetAggregateSaleInfo; llsaleinfo.h; read as behaviour).
/// </summary>
public static class SaleRules
{
    /// <summary>The permission bit of transfer, and the price the official viewer sends when the price box is left undecided.</summary>
    public const uint PermTransfer = 1u << 13;
    public const int DefaultPrice = 10;

    /// <summary>The window's view of the selected roots. <paramref name="canChange"/> is the agent owning them all (a group's powers need the
    /// group's rights, which this viewer does not read) and <paramref name="allTransfer"/> that every root may be transferred.</summary>
    public static SaleView ViewOf(IReadOnlyList<SaleNode> roots, bool canChange, bool allTransfer)
    {
        if (roots.Count == 0)
            return new(false, false, 0, false, SaleKind.Copy, false, false, false);
        var first = roots[0];
        bool firstSale = first.Type != SaleKind.NotForSale;
        int forSale = roots.Count(r => r.Type != SaleKind.NotForSale);
        bool mixed = roots.Any(r => (r.Type != SaleKind.NotForSale) != firstSale);
        bool priceMixed = mixed || roots.Any(r => r.Price != first.Price);
        int price = mixed ? 0 : first.Price;
        var type = forSale > 0 && !priceMixed && firstSale ? first.Type : SaleKind.Copy;
        // owner_mask_on & PERM_TRANSFER: every root's owner mask has transfer
        bool ownerTransfer = roots.All(r => (r.OwnerMask & PermTransfer) != 0);
        if (!canChange || !ownerTransfer)
            return new(forSale > 0, mixed, price, priceMixed, type, false, false, false);
        // an old object for sale that cannot be transferred may still be taken off sale, and nothing else
        bool allowed = allTransfer || forSale > 0;
        return new(forSale > 0, mixed, price, priceMixed, type, BoxEnabled: allowed, PriceEnabled: allowed && !mixed,
            TypeEnabled: forSale > 0 && allTransfer && !priceMixed);
    }

    /// <summary>What the For Sale box, the type and the price send: the type chosen when the box is ticked, else not for sale; a price left
    /// undecided is the default price and a negative price turns the sale off.</summary>
    public static (SaleKind Type, int Price) Commit(bool boxTicked, SaleKind chosen, int? price)
    {
        var type = boxTicked ? chosen : SaleKind.NotForSale;
        int p = price ?? DefaultPrice;
        if (p < 0)
            type = SaleKind.NotForSale;
        return (type, Math.Max(0, p));
    }
}
