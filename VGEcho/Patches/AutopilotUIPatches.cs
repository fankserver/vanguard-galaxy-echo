using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Behaviour.UI.Side_Menu.SideTabs;
using Behaviour.UI.Tooltip;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VGEcho.Patches;

/// <summary>
/// Runtime UI injection for the Autopilot side-tab. On each <see cref="Autopilot"/>
/// <c>Awake</c>, appends an "Echo" section to the scroll-view content list — a
/// header (cloned from the vanilla "Economy" header for matching styling) followed
/// by our opt-in toggles and sliders, each cloned from a vanilla template row and
/// bound to the corresponding BepInEx config entry.
///
/// Toggle rows are cloned from the "Auto sell" toggle row; slider rows are cloned
/// from the "Ammo Supply" slider row — both ship with the Autopilot prefab, so
/// they're guaranteed to exist and match the panel's visual theme exactly.
///
/// The content list (<c>ScrollView/Viewport/Content</c>) stacks its children via a
/// layout group, so placement is purely sibling-order: we append and let the layout
/// position everything. A few non-obvious constraints the prefab forces on us:
///   • The cloned Toggle's <c>onValueChanged</c> carries prefab-serialised calls
///     wired to <c>Autopilot.ToggleAutoSell</c>. Public <c>RemoveAllListeners()</c>
///     only clears runtime listeners; the persistent ones have to be nuked via
///     reflection into <c>UnityEventBase.m_PersistentCalls</c> or the click
///     silently continues to flip <c>autopilotSettings.autoSell</c>.
///   • Cloned rows/headers carry a <c>Translatable</c> MonoBehaviour that re-applies
///     the source translate key on every <c>OnEnable</c>, overwriting our relabel
///     when the tab reopens. We destroy it.
///   • The cloned <c>TooltipSource</c>'s body still points at the "@AutopilotAutoSell"
///     key, so it's retargeted at our own title/body.
/// </summary>
[HarmonyPatch(typeof(Autopilot), "Awake")]
internal static class AutopilotUIPatches
{
    private const string AutoRefineRowName = "vgecho_autoRefineRow";
    private const string RefineryRouteRowName = "vgecho_refineryRouteRow";
    private const string EchoHeaderName = "vgecho_sectionHeader";
    private const string EchoHeaderLabel = "VGEcho";

    // Tooltip body strings use the game's #word# highlight convention — the
    // regex in Translation.Translate replaces #text# with <color=#FFD100>text
    // </color> (yellow), matching the vanilla "@Autopilot*Description" keys.
    // Keep titles as plain text (the vanilla titles like "Run Missions" have
    // no highlights).
    private const string AutoRefineLabel = "Auto-refine on dock";
    private const string AutoRefineTooltip =
        "If enabled, #ECHO# will turn on the station's #Auto-Refine# toggle when docking at a " +
        "station with a #refinery#, so pending ore refines passively.";

    private const string RefineryRouteLabel = "Divert to refinery";
    private const string RefineryRouteTooltip =
        "If enabled, #ECHO# will divert to the nearest station with a #refinery# when cargo " +
        "contains #ore# instead of returning to the #Home Station#.";

    private const string AutoSafeCrackerRowName = "vgecho_autoSafeCrackerRow";
    private const string AutoSafeCrackerLabel = "Auto Crackshot Drone";
    private const string AutoSafeCrackerTooltip =
        "If enabled, #ECHO# will auto-fire the #Crackshot Drone# at the closest #asteroid# " +
        "with surface ore while autopilot is engaged. Same cooldown, same payload, no manual " +
        "#targeting#.";

    private const string AutoLbrtrRowName = "vgecho_autoLbrtrRow";
    private const string AutoLbrtrLabel = "Auto LB-RTR Bot";
    private const string AutoLbrtrTooltip =
        "If enabled, #ECHO# will auto-fire the #LB-RTR Bot# at the closest #wreck# with salvage " +
        "while autopilot is engaged. Same cooldown, same payload, no manual #targeting#.";

    private const string AutoLbrtrRangeRowName = "vgecho_autoLbrtrRangeRow";
    private const string AutoLbrtrRangeLabel = "LB-RTR range";
    private const string AutoSafeCrackerRangeRowName = "vgecho_autoSafeCrackerRangeRow";
    private const string AutoSafeCrackerRangeLabel = "Crackshot range";

    // Accessed via reflection because the shipping game DLL may keep these fields
    // non-public even though the publicized stub makes them appear public.
    private static readonly AccessTools.FieldRef<Autopilot, Toggle> AutoSellToggleRef =
        AccessTools.FieldRefAccess<Autopilot, Toggle>("autoSellToggle");

    private static readonly AccessTools.FieldRef<Autopilot, Slider> AmmoSliderRef =
        AccessTools.FieldRefAccess<Autopilot, Slider>("ammoMinutesSlider");

    // UnityEventBase internals for scrubbing persistent (prefab-serialised)
    // listeners. Cached on first use.
    private static FieldInfo? _persistentCallsField;
    private static MethodInfo? _persistentCallsClear;

    [HarmonyPostfix]
    private static void Awake_Postfix(Autopilot __instance)
    {
        try
        {
            InjectToggles(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[autopilot-ui] Awake postfix failed: {e}");
        }
    }

    private static void InjectToggles(Autopilot panel)
    {
        var sourceToggle = AutoSellToggleRef(panel);
        if (sourceToggle == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle is null; skipping UI injection");
            return;
        }

        // sourceToggle is the Autosell row's Toggle; climb to the row GameObject.
        var sourceRow = sourceToggle.transform.parent;
        if (sourceRow == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle has no parent; aborting");
            return;
        }
        // Defensive: if a future prefab restructure puts the Toggle at the row
        // level itself, climb one more so we clone the enclosing row.
        if (sourceRow.GetComponent<Toggle>() != null && sourceRow.parent != null)
        {
            sourceRow = sourceRow.parent;
        }

        // The scroll-view content list. In 0.8.1 its children stack top-to-bottom
        // via a layout group, so placement is by sibling order — append, don't
        // position by RectTransform.
        var container = sourceRow.parent;
        if (container == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] source row has no parent; aborting");
            return;
        }

        // Idempotency: our section header marks a completed injection.
        if (container.Find(EchoHeaderName) != null) return;

        // Locate the ammo slider row (template for our sliders).
        var ammoSlider = AmmoSliderRef(panel);
        Transform? ammoSliderRow = null;
        if (ammoSlider != null)
        {
            ammoSliderRow = ammoSlider.transform.parent;
            // The slider may have the same parent-ascent pattern as the toggle.
            if (ammoSliderRow != null && ammoSliderRow.GetComponent<Slider>() != null && ammoSliderRow.parent != null)
            {
                ammoSliderRow = ammoSliderRow.parent;
            }
        }

        // Our own "Echo" section header, then the toggles/sliders grouped beneath it —
        // mirroring the vanilla General / Activities / Economy / Crew sections.
        InjectSectionHeader(container, EchoHeaderLabel);

        var cfg = Plugin.Instance;
        InjectRow(container, sourceRow, AutoRefineRowName,
            AutoRefineLabel, AutoRefineTooltip, cfg.CfgAutopilotAutoRefine);
        InjectRow(container, sourceRow, RefineryRouteRowName,
            RefineryRouteLabel, RefineryRouteTooltip, cfg.CfgAutopilotRefineryRoute);
        InjectRow(container, sourceRow, AutoLbrtrRowName,
            AutoLbrtrLabel, AutoLbrtrTooltip, cfg.CfgAutopilotAutoLbrtr);
        if (ammoSliderRow != null)
        {
            InjectSliderRow(container, ammoSliderRow, AutoLbrtrRangeRowName,
                AutoLbrtrRangeLabel, cfg.CfgAutopilotAutoLbrtrRange);
        }
        InjectRow(container, sourceRow, AutoSafeCrackerRowName,
            AutoSafeCrackerLabel, AutoSafeCrackerTooltip, cfg.CfgAutopilotAutoSafeCracker);
        if (ammoSliderRow != null)
        {
            InjectSliderRow(container, ammoSliderRow, AutoSafeCrackerRangeRowName,
                AutoSafeCrackerRangeLabel, cfg.CfgAutopilotAutoSafeCrackerRange);
        }
    }

    /// <summary>
    /// Clone an existing section header (preferring "Economy") so our section
    /// matches the vanilla header styling, relabel it, and append it. The cloned
    /// <see cref="Behaviour.Util.Translatable"/> is destroyed so it can't revert
    /// our text to the source translate key on the next enable.
    /// </summary>
    private static void InjectSectionHeader(Transform container, string title)
    {
        Transform? template = FindHeaderTemplate(container);
        if (template == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] no section header to clone; skipping Echo header");
            return;
        }

        var clone = UnityEngine.Object.Instantiate(template.gameObject, container);
        clone.name = EchoHeaderName;
        clone.transform.SetAsLastSibling();

        foreach (var translatable in clone.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(translatable);
        }

        var text = clone.GetComponentInChildren<TMP_Text>(includeInactive: true);
        if (text != null) text.text = title;
        else Plugin.Log.LogWarning("[autopilot-ui] cloned header has no TMP_Text to relabel");
    }

    // Prefer the Economy header (its name carries a trailing space in 0.8.1);
    // fall back to any child whose name contains "Header".
    private static Transform? FindHeaderTemplate(Transform container)
    {
        for (int i = 0; i < container.childCount; i++)
        {
            if (container.GetChild(i).name.TrimEnd() == "EconomyHeader")
                return container.GetChild(i);
        }
        for (int i = 0; i < container.childCount; i++)
        {
            if (container.GetChild(i).name.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0)
                return container.GetChild(i);
        }
        return null;
    }

    /// <summary>
    /// Convenience overload that binds the cloned Toggle directly to a
    /// <see cref="ConfigEntry{Boolean}"/>. Reads from / writes to
    /// <paramref name="config"/>.Value.
    /// </summary>
    private static void InjectRow(Transform container, Transform sourceRow,
        string cloneName, string labelText, string tooltipText,
        ConfigEntry<bool> config)
    {
        InjectRow(container, sourceRow, cloneName, labelText, tooltipText,
            getValue: () => config.Value,
            setValue: isOn => config.Value = isOn);
    }

    /// <summary>
    /// Clone <paramref name="sourceRow"/> into <paramref name="container"/> (as the
    /// last sibling, so the content list's layout group positions it), relabel the
    /// primary TMP_Text to <paramref name="labelText"/>, retarget every cloned
    /// <see cref="TooltipSource"/> at our own title/body, scrub all prefab-wired
    /// handlers, and drive the cloned Toggle via the supplied getter/setter. Using
    /// lambdas (rather than a single ConfigEntry) lets one toggle mirror multiple
    /// configs at once if a future feature needs it.
    /// </summary>
    private static void InjectRow(Transform container, Transform sourceRow,
        string cloneName, string labelText, string tooltipText,
        Func<bool> getValue, Action<bool> setValue)
    {
        // Idempotency: if we already added one on a previous Awake of the same
        // panel GameObject, don't stack a duplicate.
        if (container.Find(cloneName) != null) return;

        var cloneGO = UnityEngine.Object.Instantiate(sourceRow.gameObject, container);
        cloneGO.name = cloneName;
        // Appended last; the content list's layout group positions it in order.
        cloneGO.transform.SetAsLastSibling();

        var toggle = cloneGO.GetComponentInChildren<Toggle>(includeInactive: true);
        if (toggle == null)
        {
            Plugin.Log.LogWarning($"[autopilot-ui] cloned row '{cloneName}' has no Toggle; aborting");
            UnityEngine.Object.Destroy(cloneGO);
            return;
        }

        // Full scrub of the cloned Toggle's onValueChanged so clicks on the
        // checkbox don't also fire the source row's prefab-wired handler.
        ScrubUnityEvent(toggle.onValueChanged);

        // Rename the primary (first non-empty) TMP_Text; disable raycast on the
        // rest so they can't intercept anything. Attach LabelClickProxy so
        // clicks on the label have a no-op handler here rather than bubbling.
        var clonedTexts = cloneGO.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        var primary = clonedTexts.FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
        if (primary != null)
        {
            primary.text = labelText;
            var proxy = primary.gameObject.AddComponent<LabelClickProxy>();
            proxy.target = toggle;
        }
        else
        {
            Plugin.Log.LogWarning($"[autopilot-ui] no TMP_Text with non-empty text in clone '{cloneName}'");
        }
        foreach (var text in clonedTexts)
        {
            if (text != primary)
            {
                text.raycastTarget = false;
            }
        }

        // Translatable.OnEnable re-applies the source translate key on each
        // enable — would overwrite our rename when the tab is re-opened.
        foreach (var translatable in cloneGO.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(translatable);
        }
        // The source row ships TooltipSource on both the label and the Toggle
        // (so hovering anywhere on the row shows the same tooltip). Retarget
        // each of them at our own title + body. TooltipSource accepts raw
        // strings — Translation.TranslateOnly returns the input unchanged when
        // it doesn't start with '@', so plain English just renders as-is.
        foreach (var tooltip in cloneGO.GetComponentsInChildren<TooltipSource>(includeInactive: true))
        {
            tooltip.Title = labelText;
            tooltip.BodyText = tooltipText;
        }

        toggle.SetIsOnWithoutNotify(getValue());
        toggle.onValueChanged.AddListener(isOn =>
        {
            if (getValue() == isOn) return;
            setValue(isOn);
            Plugin.Instance.Config.Save();
        });
    }

    /// <summary>
    /// Inject a slider row for a <see cref="ConfigEntry{float}"/> into the autopilot
    /// side-tab. Clones the vanilla "Ammo Supply" slider row (whose parent is
    /// <paramref name="ammoSliderRow"/>) for consistent styling, then retargets the
    /// label, slider binding, and value display to the supplied config entry.
    /// </summary>
    private static void InjectSliderRow(Transform container, Transform ammoSliderRow,
        string cloneName, string labelText, ConfigEntry<float> config)
    {
        if (container.Find(cloneName) != null) return;

        var cloneGO = UnityEngine.Object.Instantiate(ammoSliderRow.gameObject, container);
        cloneGO.name = cloneName;
        cloneGO.transform.SetAsLastSibling();

        // Strip Translatables so they can't revert our labels.
        foreach (var translatable in cloneGO.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(translatable);
        }
        // Strip TooltipSources — slider rows don't need tooltips.
        foreach (var tooltip in cloneGO.GetComponentsInChildren<TooltipSource>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(tooltip);
        }

        // Retarget the label: find the first non-empty TMP_Text. The ammo slider
        // has one short numeric text (the value) and one longer descriptive text
        // (the label). Heuristic: length ≤ 4 and starts with a digit → value.
        var allTexts = cloneGO.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        TMP_Text? labelTextComp = null;
        TMP_Text? valueTextComp = null;
        foreach (var t in allTexts)
        {
            if (!string.IsNullOrEmpty(t.text) && t.text.Length <= 4 && char.IsDigit(t.text[0]))
            {
                valueTextComp = t;
            }
            else if (!string.IsNullOrEmpty(t.text))
            {
                labelTextComp = t;
            }
        }
        if (labelTextComp != null)
        {
            labelTextComp.text = labelText;
        }

        // Retarget the slider.
        var slider = cloneGO.GetComponentInChildren<Slider>(includeInactive: true);
        if (slider == null)
        {
            Plugin.Log.LogWarning($"[autopilot-ui] cloned slider row '{cloneName}' has no Slider; aborting");
            UnityEngine.Object.Destroy(cloneGO);
            return;
        }

        // Safe-cast: if AcceptableValues is null or somehow a different type (e.g.
        // a game update swapped the config description), fall through with the
        // vanilla slider's existing min/max rather than crashing the panel.
        if (config.Description.AcceptableValues is AcceptableValueRange<float> range)
        {
            slider.minValue = range.MinValue;
            slider.maxValue = range.MaxValue;
            slider.wholeNumbers = true;
        }

        // Scrub the slider's onValueChanged (vanilla has it wired to ammo logic).
        ScrubUnityEvent(slider.onValueChanged);

        slider.SetValueWithoutNotify(config.Value);
        if (valueTextComp != null)
        {
            valueTextComp.text = config.Value.ToString("F0");
        }

        slider.onValueChanged.AddListener(val =>
        {
            config.Value = val;
            if (valueTextComp != null) valueTextComp.text = val.ToString("F0");
            Plugin.Instance.Config.Save();
        });
    }

    /// <summary>
    /// Wipe both runtime listeners and prefab-serialised persistent calls from
    /// a UnityEvent. The public <c>RemoveAllListeners</c> only clears runtime
    /// listeners; persistent calls survive and keep firing their original
    /// targets. Reach into <c>UnityEventBase.m_PersistentCalls</c> and clear
    /// the internal list to nuke them fully.
    /// </summary>
    private static void ScrubUnityEvent(UnityEventBase evt)
    {
        var removeAll = evt.GetType().GetMethod(
            "RemoveAllListeners", BindingFlags.Instance | BindingFlags.Public);
        removeAll?.Invoke(evt, null);

        int persistentCount = evt.GetPersistentEventCount();
        for (int i = 0; i < persistentCount; i++)
        {
            evt.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        _persistentCallsField ??= typeof(UnityEventBase).GetField(
            "m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_persistentCallsField == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] UnityEventBase.m_PersistentCalls field not found; " +
                                  "persistent scrub fell back to disable-only");
            return;
        }
        var persistentCalls = _persistentCallsField.GetValue(evt);
        if (persistentCalls == null) return;

        _persistentCallsClear ??= persistentCalls.GetType().GetMethod(
            "Clear", BindingFlags.Instance | BindingFlags.Public);
        if (_persistentCallsClear == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] PersistentCallGroup.Clear not found; " +
                                  "persistent scrub fell back to disable-only");
            return;
        }
        _persistentCallsClear.Invoke(persistentCalls, null);
    }
}

/// <summary>
/// Absorbs pointer clicks on the cloned label. Without an IPointerClickHandler
/// on the label's own GameObject, Unity's EventSystem walks up the parent chain
/// looking for one — and depending on the raycast resolution and sibling order,
/// the click can land on a neighbouring Selectable (observed: Disable-Travel
/// toggle) or a parent handler (observed: SideTabAutopilot, which flips the
/// autoPlay master toggle). This handler claims the click for the label's
/// GameObject and deliberately does nothing with it — per the user,
/// "text is text, no actions".
/// </summary>
public sealed class LabelClickProxy : MonoBehaviour, IPointerClickHandler
{
    // Kept as a reference for future behaviour changes (e.g. restoring
    // label-click-toggles-checkbox UX would wire target.isOn = !target.isOn).
    public Toggle? target;

    public void OnPointerClick(PointerEventData eventData)
    {
        // Consume the click. Intentionally no behaviour.
    }
}
