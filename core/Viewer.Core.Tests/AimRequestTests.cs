// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text.Json;
using Viewer.Automation;
using Viewer.Core.Camera;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The body of the aim command: what it accepts, its defaults, and why it refuses. Every member builds its own values: no shared state.</summary>
public class AimRequestTests
{
    private static (bool Ok, AimRequest Req, string Error) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        bool ok = AimRequest.TryParse(doc.RootElement.Clone(), out var req, out var error);
        return (ok, req, error);
    }

    private const string Person = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void An_aim_at_a_person_by_id_takes_its_values()
    {
        var (ok, r, error) = Parse($$"""{"op":"aim","target":"avatar","id":"{{Person}}","point":"chest","distance":3,"around_deg":90,"above_deg":10,"fov_deg":35}""");
        Assert.True(ok, error);
        Assert.Equal("aim", r.Op);
        Assert.Equal("avatar", r.Target);
        Assert.Equal(Guid.Parse(Person), r.Id);
        Assert.Equal(AimPoint.Chest, r.Point);
        Assert.Equal(3f, r.Distance);
        Assert.Equal(90f, r.AroundDeg);
        Assert.Equal(10f, r.AboveDeg);
        Assert.Equal(35f, r.FovDeg);
    }

    [Fact]
    public void The_defaults_are_an_aim_at_a_person_from_the_front_with_a_forty_degree_view()
    {
        var (ok, r, error) = Parse($$"""{"id":"{{Person}}","distance":2}""");
        Assert.True(ok, error);
        Assert.Equal("aim", r.Op);
        Assert.Equal("avatar", r.Target);
        Assert.Equal(AimPoint.Body, r.Point);
        Assert.Equal(0f, r.AroundDeg);
        Assert.Equal(0f, r.AboveDeg);
        Assert.Equal(40f, r.FovDeg);
        Assert.Equal(0.1f, r.MaxOffset);
    }

    [Fact]
    public void The_smallest_share_of_the_frame_the_target_must_fill_depends_on_the_part_aimed_at()
    {
        Assert.Equal(0.25f, Parse($$"""{"id":"{{Person}}","distance":1,"point":"head"}""").Req.MinFill);
        Assert.Equal(0.5f, Parse($$"""{"id":"{{Person}}","distance":3,"point":"body"}""").Req.MinFill);
        Assert.Equal(0.4f, Parse($$"""{"id":"{{Person}}","distance":3,"point":"body","min_fill":0.4}""").Req.MinFill);
    }

    [Fact]
    public void A_person_can_be_named_and_an_object_given_by_its_local_id()
    {
        var (ok, r, error) = Parse("""{"name":"Some Person","distance":2,"point":"head"}""");
        Assert.True(ok, error);
        Assert.Equal("Some Person", r.Name);
        var (ok2, r2, error2) = Parse("""{"target":"object","local":4711,"point":"centre","distance":5}""");
        Assert.True(ok2, error2);
        Assert.Equal(4711u, r2.Local);
        Assert.Equal(AimPoint.Centre, r2.Point);
    }

    [Fact]
    public void State_and_release_need_no_target()
    {
        var (ok, r, error) = Parse("""{"op":"state"}""");
        Assert.True(ok, error);
        Assert.Equal("state", r.Op);
        var (ok2, r2, error2) = Parse("""{"op":"release"}""");
        Assert.True(ok2, error2);
        Assert.Equal("release", r2.Op);
    }

    [Theory]
    [InlineData("""{"distance":3}""", "target")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555"}""", "distance")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":0}""", "distance")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":500}""", "distance")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":3,"point":"feet"}""", "point")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":3,"above_deg":90}""", "above_deg")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":3,"fov_deg":3}""", "fov_deg")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":3,"fov_deg":170}""", "fov_deg")]
    [InlineData("""{"op":"spin"}""", "op")]
    [InlineData("""{"target":"sky","id":"11111111-2222-3333-4444-555555555555","distance":3}""", "target")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","distance":3,"min_fill":2}""", "min_fill")]
    public void A_body_that_cannot_be_acted_on_is_refused_with_the_name_of_the_value(string json, string mentioned)
    {
        var (ok, _, error) = Parse(json);
        Assert.False(ok);
        Assert.Contains(mentioned, error);
    }

    [Fact]
    public void A_body_that_is_not_an_object_is_refused()
    {
        var (ok, _, error) = Parse("[1,2]");
        Assert.False(ok);
        Assert.NotEqual("", error);
    }
}
