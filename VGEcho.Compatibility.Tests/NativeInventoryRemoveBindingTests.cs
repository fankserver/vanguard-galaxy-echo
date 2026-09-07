using System;
using System.Reflection;
using VGEcho.Patches;
using Xunit;

namespace VGEcho.Compatibility.Tests;

/// <summary>
/// Exercises the linked production binding helper
/// (<see cref="NativeInventoryRemoveBinding"/>) against synthetic inventories
/// that mimic the game's overload sets. No Unity, no BepInEx, no game assembly.
/// </summary>
public class NativeInventoryRemoveBindingTests
{
    private sealed class Item
    {
        public string Name { get; init; } = "";
    }

    private sealed class Stack
    {
        public Item? Item { get; init; }
    }

    /// <summary>
    /// Mirrors game 0.8.2.3: the removal overload takes the favourite-skip flag,
    /// and an unrelated <c>Remove</c> overload sits next to it.
    /// </summary>
    private sealed class CurrentInventory
    {
        public Item? LastItem;
        public int LastAmount;
        public bool LastSkipFavourited;
        public int Calls;

        public int Remove(Item item, int amount, bool skipFavourited = false)
        {
            LastItem = item;
            LastAmount = amount;
            LastSkipFavourited = skipFavourited;
            Calls++;
            return amount;
        }

        public bool Remove(Stack stack, int count) => stack.Item != null && count > 0;
    }

    /// <summary>Mirrors the older shape the committed publicized stub still declares.</summary>
    private sealed class LegacyInventory
    {
        public int Remove(Item item, int amount) => item == null ? 0 : amount;
    }

    private sealed class WrongReturnTypeInventory
    {
        public bool Remove(Item item, int amount, bool skipFavourited = false) =>
            item != null && amount > 0 && !skipFavourited;
    }

    [Fact]
    public void ResolveRemove_FindsTheThreeArgumentOverload()
    {
        MethodInfo remove = NativeInventoryRemoveBinding.ResolveRemove(typeof(CurrentInventory), typeof(Item));

        Assert.Equal("Remove", remove.Name);
        Assert.False(remove.IsStatic);
        Assert.Equal(typeof(int), remove.ReturnType);
        Assert.Equal(
            new[] { typeof(Item), typeof(int), typeof(bool) },
            Array.ConvertAll(remove.GetParameters(), p => p.ParameterType));
    }

    [Fact]
    public void ResolveRemove_RejectsAnInventoryThatOnlyHasTheLegacyTwoArgumentOverload()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => NativeInventoryRemoveBinding.ResolveRemove(typeof(LegacyInventory), typeof(Item)));

        Assert.Contains("Remove(Item, int, bool)", error.Message);
    }

    [Fact]
    public void ResolveRemove_RejectsAnOverloadThatDoesNotReturnTheRemovedCount()
    {
        Assert.Throws<InvalidOperationException>(
            () => NativeInventoryRemoveBinding.ResolveRemove(typeof(WrongReturnTypeInventory), typeof(Item)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateOpenRemoveDelegate_ForwardsTheNativeFlagUnchanged(bool skipFavourited)
    {
        MethodInfo remove = NativeInventoryRemoveBinding.ResolveRemove(typeof(CurrentInventory), typeof(Item));
        Func<CurrentInventory, Item, int, bool, int> bound =
            NativeInventoryRemoveBinding.CreateOpenRemoveDelegate<CurrentInventory, Item>(remove);

        var inventory = new CurrentInventory();
        var item = new Item { Name = "iron-ore" };

        int removed = bound(inventory, item, 17, skipFavourited);

        Assert.Equal(17, removed);
        Assert.Equal(1, inventory.Calls);
        Assert.Same(item, inventory.LastItem);
        Assert.Equal(17, inventory.LastAmount);
        Assert.Equal(skipFavourited, inventory.LastSkipFavourited);
    }

    [Fact]
    public void CreateOpenRemoveDelegate_BindsOpenSoTheReceiverIsAnArgument()
    {
        MethodInfo remove = NativeInventoryRemoveBinding.ResolveRemove(typeof(CurrentInventory), typeof(Item));
        Func<CurrentInventory, Item, int, bool, int> bound =
            NativeInventoryRemoveBinding.CreateOpenRemoveDelegate<CurrentInventory, Item>(remove);

        var cargo = new CurrentInventory();
        var globalInventory = new CurrentInventory();

        bound(cargo, new Item(), 3, true);
        bound(globalInventory, new Item(), 5, false);

        Assert.Equal(1, cargo.Calls);
        Assert.Equal(3, cargo.LastAmount);
        Assert.True(cargo.LastSkipFavourited);
        Assert.Equal(1, globalInventory.Calls);
        Assert.Equal(5, globalInventory.LastAmount);
        Assert.False(globalInventory.LastSkipFavourited);
    }

    [Fact]
    public void CreateOpenRemoveDelegate_FailsClosedWhenTheMethodShapeDiffers()
    {
        MethodInfo mismatched = typeof(CurrentInventory).GetMethod(
            "Remove",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(Stack), typeof(int) },
            modifiers: null)!;

        var error = Assert.Throws<InvalidOperationException>(
            () => NativeInventoryRemoveBinding.CreateOpenRemoveDelegate<CurrentInventory, Item>(mismatched));

        Assert.Contains("does not match the expected", error.Message);
    }
}
