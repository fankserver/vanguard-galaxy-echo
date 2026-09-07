using System;
using System.Reflection;

namespace VGEcho.Patches;

/// <summary>
/// Late-bound access to the game's <c>Inventory.Remove(InventoryItemType, int, bool)</c>.
///
/// VGEcho compiles against the publicized <c>Assembly-CSharp.dll</c> stub committed
/// at <c>VGEcho/lib/</c>, which still declares the older two-argument
/// <c>Remove(InventoryItemType, int)</c>. Game 0.8.2.3 replaced it with
/// <c>Remove(InventoryItemType, int, bool skipFavourited = false)</c>, so any
/// direct call compiled from this repo would bake a member reference the live
/// game no longer carries and throw <c>MissingMethodException</c> at runtime.
///
/// Resolving the exact three-argument overload reflectively and caching it as a
/// typed open-instance delegate keeps the plugin buildable against the old stub
/// while calling the real method. The resolve/create pair is deliberately
/// fail-closed: a shape mismatch throws instead of silently degrading, so the
/// startup log names the incompatibility rather than the deposit path
/// misbehaving mid-session.
///
/// Kept free of Unity, BepInEx and Harmony types so the compatibility test
/// suite can exercise it on a bare .NET host against a synthetic inventory.
/// </summary>
internal static class NativeInventoryRemoveBinding
{
    internal const string RemoveMethodName = "Remove";

    /// <summary>
    /// Finds the <c>Remove(item, amount, skipFavourited) -> int</c> instance
    /// overload on <paramref name="inventoryType"/>. Exact parameter-type match:
    /// the sibling <c>Remove(InventoryItem, int) -> bool</c> overload and the old
    /// two-argument form are both rejected.
    /// </summary>
    /// <exception cref="InvalidOperationException">The overload is absent or has a different shape.</exception>
    internal static MethodInfo ResolveRemove(Type inventoryType, Type itemType)
    {
        if (inventoryType == null) throw new ArgumentNullException(nameof(inventoryType));
        if (itemType == null) throw new ArgumentNullException(nameof(itemType));

        MethodInfo? remove = inventoryType.GetMethod(
            RemoveMethodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { itemType, typeof(int), typeof(bool) },
            modifiers: null);

        if (remove == null || remove.ReturnType != typeof(int))
        {
            throw new InvalidOperationException(
                $"[autopilot-stack] {inventoryType.FullName}.{RemoveMethodName}({itemType.Name}, int, bool) " +
                "returning int not found — game version mismatch?");
        }

        return remove;
    }

    /// <summary>
    /// Binds <paramref name="remove"/> as an open-instance delegate whose first
    /// argument is the receiver, so callers can forward the native
    /// <c>skipFavourited</c> flag through unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">The method does not match the delegate shape.</exception>
    internal static Func<TInventory, TItem, int, bool, int> CreateOpenRemoveDelegate<TInventory, TItem>(MethodInfo remove)
    {
        if (remove == null) throw new ArgumentNullException(nameof(remove));

        try
        {
            return (Func<TInventory, TItem, int, bool, int>)remove.CreateDelegate(
                typeof(Func<TInventory, TItem, int, bool, int>));
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(
                $"[autopilot-stack] {remove.DeclaringType?.FullName}.{remove.Name} does not match the expected " +
                "(inventory, item, amount, skipFavourited) -> int shape — game version mismatch?", e);
        }
    }
}
