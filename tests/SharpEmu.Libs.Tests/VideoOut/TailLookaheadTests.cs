// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class TailLookaheadTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type CommandType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
    private static readonly Type ItemType = CommandType.GetNestedType("Item", BindingFlags.NonPublic)!;
    private static readonly Type KindType = CommandType.GetNestedType("Kind", BindingFlags.NonPublic)!;
    private static readonly Type SliceType = typeof(VulkanVideoPresenter).GetNestedType("RenderSlice", BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("Call", false)]
    [InlineData("Write", false)]
    [InlineData("Call", true)]
    public void RefillStopsBeforeEveryObservationBoundary(string kind, bool sync)
    {
        var command = CreateCommand();
        var first = Item("Deferred");
        var boundary = Item(kind, sync);
        var after = Item("Deferred");
        Incoming(command).Add(first);
        Incoming(command).Add(boundary);
        Incoming(command).Add(after);

        Assert.True(Refill(command));
        Assert.Same(first, CuttingItems(command)[0]);
        Assert.Equal(1, Field<int>(command, "_drainIndex"));
        var draining = Field<IList>(command, "_draining");
        Assert.Same(boundary, draining[1]);
        Assert.Same(after, draining[2]);
        Assert.False(Refill(command));
        Assert.Single(CuttingItems(command).Cast<object>());
    }

    [Fact]
    public void EmptyPublishedQueueReturnsImmediatelyAndLeavesTailOpen()
    {
        var command = CreateCommand();
        Assert.False(Refill(command));
        Assert.Empty(CuttingItems(command));
        // Publication after the first attempt is only eligible on a later attempt.
        var next = Item("Deferred");
        Incoming(command).Add(next);
        Assert.True(Refill(command));
        Assert.Same(next, CuttingItems(command)[0]);
    }

    [Fact]
    public void RefillConsumesExistingDrainBeforeNewlyPublishedItems()
    {
        var command = CreateCommand();
        var old = Item("Deferred");
        var newer = Item("Deferred");
        Field<IList>(command, "_draining").Add(old);
        Incoming(command).Add(newer);
        Assert.True(Refill(command));
        Assert.Equal(new[] { old, newer }, CuttingItems(command).Cast<object>());
        Assert.Empty(Incoming(command));
    }

    [Fact]
    public void PublishedSerialBoundaryIsNeverExecutedOrRemovedByRefill()
    {
        var command = CreateCommand();
        var boundary = Item("Call", true);
        ItemType.GetField("Action")!.SetValue(boundary, (Action)(() => throw new Exception("Must not execute")));
        Incoming(command).Add(boundary);
        Assert.False(Refill(command));
        Assert.Same(boundary, Field<IList>(command, "_draining")[0]);
        Assert.Equal(0, Field<int>(command, "_drainIndex"));
    }

    [Fact]
    public void ContendedPublicationLockDoesNotBlockTailRefill()
    {
        var command = CreateCommand();
        Incoming(command).Add(Item("Deferred"));
        var gate = Field<object>(command, "_lock");
        bool? result = null;
        var thread = new Thread(() => result = Refill(command)) { IsBackground = true };
        lock (gate)
        {
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "Refill waited for the producer lock.");
            Assert.False(result);
        }
        Assert.Single(Incoming(command).Cast<object>());
        Assert.Empty(CuttingItems(command));
    }

    [Theory]
    [InlineData("Call", false)]
    [InlineData("Write", false)]
    [InlineData("Call", true)]
    public void FlushedShortTailRemainsBeforeTheUnconsumedBoundary(string kind, bool sync)
    {
        var command = CreateCommand();
        var first = Item("Deferred");
        var second = Item("Deferred");
        var boundary = Item(kind, sync);
        Incoming(command).Add(first);
        Incoming(command).Add(second);
        Incoming(command).Add(boundary);
        Assert.True(Refill(command));
        Assert.False((bool)CommandType.GetMethod("FlushCutting", Members)!.Invoke(command, null)!);
        var units = Field<IList>(command, "_units");
        Assert.Equal(2, units.Count);
        Assert.Same(first, units[0]!.GetType().GetField("Item")!.GetValue(units[0]));
        Assert.Same(second, units[1]!.GetType().GetField("Item")!.GetValue(units[1]));
        Assert.Same(boundary, Field<IList>(command, "_draining")[2]);
    }
    [Fact]
    public void FillUnitsKeepsCallsAndWritesInOrderAndLookaheadStopsAtSyncRun()
    {
        var command = CreateCommand();
        var presenterType = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
        var poolType = typeof(VulkanVideoPresenter).GetNestedType("RenderWorkerPool", BindingFlags.NonPublic)!;
        var presenter = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(presenterType);
        var pool = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(poolType);
        poolType.GetField("_threads", Members)!.SetValue(pool, new Thread[1]);
        presenterType.GetField("_workers", Members)!.SetValue(presenter, pool);
        CommandType.GetField("_presenter", Members)!.SetValue(command, presenter);
        object[] items = [Item("Deferred"), Item("Call"), Item("Deferred"), Item("Write"),
            Item("Deferred"), Item("Call", true), Item("Deferred")];
        foreach (var item in items) Incoming(command).Add(item);

        CommandType.GetMethod("FillUnits", Members)!.Invoke(command, [256]);

        var units = Field<IList>(command, "_units");
        Assert.Equal(items.Take(6), units.Cast<object>().Select(unit => unit.GetType().GetField("Item")!.GetValue(unit)));
        var enabled = (bool)CommandType.GetField("TailLookahead", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal(enabled ? 6 : 0, Field<int>(command, "_drainIndex"));
        if (enabled)
            Assert.Same(items[6], Field<IList>(command, "_draining")[6]);
        else
            Assert.Same(items[6], CuttingItems(command)[0]);
    }
    private static object CreateCommand()
    {
        var command = Activator.CreateInstance(CommandType, Members, binder: null, args: new object?[] { null, null, null, null, null }, culture: null)!;
        CommandType.GetField("_cutting", Members)!.SetValue(command, Activator.CreateInstance(SliceType, nonPublic: true));
        return command;
    }

    private static object Item(string kind, bool sync = false)
    {
        var item = Activator.CreateInstance(ItemType)!;
        ItemType.GetField("Kind")!.SetValue(item, Enum.Parse(KindType, kind));
        ItemType.GetField("Sync")!.SetValue(item, sync);
        return item;
    }

    private static T Field<T>(object command, string name) => (T)CommandType.GetField(name, Members)!.GetValue(command)!;
    private static IList Incoming(object command) => Field<IList>(command, "_incoming");
    private static IList CuttingItems(object command) => (IList)SliceType.GetField("Items")!.GetValue(Field<object>(command, "_cutting"))!;
    private static bool Refill(object command) => (bool)CommandType.GetMethod("TryRefillTail", Members)!.Invoke(command, null)!;
}
