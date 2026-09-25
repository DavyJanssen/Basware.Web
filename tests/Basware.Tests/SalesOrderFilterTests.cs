using System;
using WpfAppBaswareLogin.Services;
using WpfAppBaswareLogin.ViewModels;
using Xunit;

namespace Basware.Tests;

public class SalesOrderFilterTests
{
    private static EdiOrderSummary Order => new(1, "A", "D", null, new DateTime(2026, 9, 30, 23, 59, 0), ["GSK", "Other"], 0, "123")
    { DeliveryPointSearchNames = ["Vestiging Zolder"], DeliveryPointShortCode = "ZOL" };

    [Fact]
    public void CombinedFiltersIncludeEntireEndDateAndMixedLaboratories()
    {
        Assert.True(SalesOrderFilter.Matches(Order, new(2026, 9, 20), new(2026, 9, 30), "gsk", " zolder "));
        Assert.False(SalesOrderFilter.Matches(Order, null, null, "GSK", "Brussel"));
        Assert.False(SalesOrderFilter.Matches(Order, null, new(2026, 9, 29), "", ""));
        Assert.False(SalesOrderFilter.Matches(Order, new(2026, 10, 1), null, "", ""));
    }

    [Fact]
    public void MissingDateOnlyMatchesWithoutDateFilter()
    {
        var order = Order with { RequestedDeliveryDate = null };
        Assert.True(SalesOrderFilter.Matches(order, null, null, "", ""));
        Assert.False(SalesOrderFilter.Matches(order, new(2026, 9, 1), null, "", ""));
        Assert.False(SalesOrderFilter.Matches(order, null, new(2026, 9, 30), "", ""));
    }

    [Fact]
    public void InvalidRangeMatchesNothing()
    {
        Assert.False(SalesOrderFilter.Matches(Order, new(2026, 10, 1), new(2026, 9, 1), "", ""));
    }

    [Fact]
    public void CheckboxNotifiesOnlyWhenChanged()
    {
        var item = new SalesOrderListItem(Order);
        int changes = 0;
        item.PropertyChanged += (_, _) => changes++;
        item.IsChecked = true;
        item.IsChecked = true;
        item.IsChecked = false;
        Assert.Equal(2, changes);
    }
}
