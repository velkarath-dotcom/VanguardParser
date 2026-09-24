using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Behaviour.UI.Tooltip;
using Source.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VGParserMod.Patches;

namespace VGParserMod
{
    // The standing readout: what is in the area, kept on screen while you fly.
    //
    // uGUI, never IMGUI (C4). An IMGUI overlay paints over everything, cannot sit in the UI layer, and only draws
    // while its OnGUI runs, which makes "leave it open and keep playing" impossible.
    //
    // ON OUR OWN CANVAS, which is a deliberate change from the original plan of parenting to the game's HUD canvas.
    // The reason that plan existed was to reuse the game's own window components, and that turned out to be
    // impossible. Without that
    // reason, our own canvas is strictly better: marked DontDestroyOnLoad it survives a dock or a system change
    // with no re-find, which was the failure V7 warned about ("it vanishes silently, which reads as the mod
    // breaking"). The font still comes from the game, cloned off a live label, so the panel reads as part of it.
    internal static class Panel
    {
        private const float Width = 1000f;
        private const float Pad = 10f;

        // ONE palette, named, rather than hex at each call site. GOLD is a capture and it is the game's own
        // Legendary tone, so it reads as the game's idea of "rare"; RED is a threat, GREEN a neighbour.
        private const string Gold = "#f2c14e";
        private const string Red = "#ff8a8a";
        private const string Green = "#8ae89a";
        private const string Bright = "#ffffff";
        private const string Dim = "#c8d4e0";
        private const string Black = "#000000";
        private const string Grey = "#8080aa";
        private const string White = "#ffffff";
        private static GameObject? _root;
        private static RectTransform? _rect;
        private static TMP_Text? _title;
        private static TMP_Text? _body;
        private static GameObject? _chartRoot;
        private static Button? _clearButton;
        private static Button? _chartToggleButton;
        private static TMP_Text? _chartToggleLabel;
        private static bool _open;
        private static bool _chartExpanded = true;
        private static bool _warned;

        internal static bool IsOpen => _open && _root != null;

        // WHO RAISED IT, which decides who may lower it. A panel the watch raised may be lowered again when the plot
        // clears; a panel the player opened stays up until they close it. And a panel the player CLOSED is not raised
        // again for the same engagement, or dismissing it would be pointless — the flag clears once the area is empty.
        internal static bool AutoOpened;
        internal static bool DismissedByHand;

        internal static void Toggle()
        {
            // Logged on every request, so a keypress that reaches us is visible even when nothing appears: the
            // first report of this panel was "not displayed", and silence cannot distinguish a hotkey that never
            // fired from a panel that failed to draw.
            Console.WriteLine($"panel toggle requested (was {(_open ? "open" : "closed")}, "
                               + $"built {(_root != null ? "yes" : "no")})");
            if (_root == null && !Build()) return;
            var root = _root!;
            _open = !_open;
            root.SetActive(_open);
            AutoOpened = false;
            DismissedByHand = !_open;
            if (_open) Refresh();
        }

        internal static void Close()
        {
            _open = false;
            if (_root != null) _root.SetActive(false);
        }

        private static void ClearCurrentDamageStats()
        {
            DamageLoggingPatches.ClearCurrentDamage();
            Refresh(0f, 0d, new Dictionary<string, DamageCategory>());
        }

        private static void ToggleChartVisibility()
        {
            if (_chartRoot == null)
            {
                return;
            }

            _chartExpanded = !_chartExpanded;
            _chartRoot.SetActive(_chartExpanded);
            if (_chartToggleLabel != null)
            {
                _chartToggleLabel.text = _chartExpanded ? "v" : ">";
            }

            if (_root != null)
            {
                Refresh();
            }
        }

        /// <summary>Lower a panel the WATCH raised. One the player opened is left alone.</summary>
        internal static void AutoHide()
        {
            if (!_open || !AutoOpened) return;
            AutoOpened = false;
            Close();
        }

        // AN ARRIVAL BORROWS THE HEADER, AND CARRIES IT. The line it replaces says what is out there, so the wave
        // sentence states the change AND what the plot holds after it (`Voice.Change`) rather than hiding the second
        // for six seconds. Amber, since gold is a capture and red is a hull shooting at us.
        private const string Alert = "#f0b84a";
        private static string? _flash;
        private static float _flashUntil;

        internal static void Flash(string line, float seconds = 6f)
        {
            _flash = line;
            _flashUntil = Time.unscaledTime + seconds;
            Refresh();
        }

        // THE HEADER IS THE AREA, THE ROWS ARE THE RULES. Every hostile hull is called whether or not it earned a
        // row: the quality rules choose what is worth a line, not what exists. The counts come from `Watch.Standing`,
        // which the wave call reads too, so the flash and the line it replaces cannot state different numbers.
        private static string Header(float dps, double damage)
        {
            return $"<b>DPS</b>  <color={Gold}>{letterFormat(dps)}</color> | <b>Total Damage</b>  <color={Gold}>{letterFormat((float)damage)}</color>";
        }

        private static string letterFormat(float number)
        {
            if (number >= 1_000_000_000_000f)
            {
                return $"{number / 1_000_000_000_000f:F1}T";
            }

            if (number >= 1_000_000_000f)
            {
                return $"{number / 1_000_000_000f:F1}B";
            }

            if (number >= 1_000_000f)
            {
                return $"{number / 1_000_000f:F1}M";
            }

            if (number >= 1_000f)
            {
                return $"{number / 1_000f:F1}K";
            }

            return $"{number:F0}";
        }
        /// <summary>Redraw, scanning for itself. For a settings change, where no tick is in hand.</summary>
        internal static void Refresh() { Refresh(0f, 0d, null); }

        /// <summary>
        /// RAISE the panel, without stealing focus. Named `Raise` rather than `Show` because `Show` is what a
        /// NOTIFICATION does (`GameToast.Show`), and the two are opposite in kind: one puts a sentence in front of the
        /// player and goes, the other puts a standing readout on screen and stays.
        /// </summary>
        internal static void Raise(bool auto = false)
        {
            if (_root == null && !Build()) return;
            if (auto && !_open) AutoOpened = true;
            _open = true;
            _root!.SetActive(true);
            Refresh();
        }

        // Rebuilt from the live set. Called at 4 Hz rather than per frame: a distance that updates 60 times a
        // second reads no better and costs a string build every frame.
        /// <summary>Redraw from a scan already taken, so one pass serves the panel and the state changes.</summary>
        internal static void Refresh(float? dps, double? damage, Dictionary<string, DamageCategory>? categories)
        {
            var body = _body;
            var title = _title;
            var rect = _rect;
            if (!IsOpen || body == null || title == null || rect == null || categories == null) return;

            title.ForceMeshUpdate();
            var head = Mathf.Max(22f, title.preferredHeight);
            if (_chartRoot == null || !_chartRoot.activeSelf)
            {
                body.text = "";
                rect.sizeDelta = new Vector2(Width, head + Pad * 2 + 8f);
                return;
            }

            // Rebuilding the row objects while the pointer is over the chart destroys the hovered element and
            // triggers TooltipSource.OnDestroy -> UITooltip.Hide, which causes the tooltip to flicker on every
            // refresh. Only suppress the rebuild while the mouse is actually over this chart area.
            if (EventSystem.current != null && _chartRoot != null && EventSystem.current.IsPointerOverGameObject())
            {
                var pointer = EventSystem.current.currentInputModule?.inputOverride;
                if (pointer == null)
                {
                    var chartRect = _chartRoot.GetComponent<RectTransform>();
                    if (chartRect != null && RectTransformUtility.RectangleContainsScreenPoint(chartRect, Input.mousePosition))
                    {
                        return;
                    }
                }
            }

            var sb = new StringBuilder();

            // AN EMPTY PANEL SAYS ONE LINE AND STOPS. It used to print the whole rejection breakdown plus a note
            // about which ranks can roll which letters, and the player's verdict on that was "all this is noise":
            // a HUD that lectures is worse than one that says little, and the tab is where a player goes to ask why.
            // V11's requirement is still met, in the place that can afford it.
            var flashing = _flash != null && Time.unscaledTime < _flashUntil;
            title.text = flashing
                ? $"<b>DPS</b>  <color={Alert}><b>{_flash}</b></color>"
                : Header(dps ?? 0f, damage ?? 0d);

            // THE BODY SITS UNDER WHATEVER THE TITLE ACTUALLY TOOK. A fixed 24 px offset assumed the header is one
            // line, and a wave call is long enough to wrap: the second line landed on top of the first contact row.
            // The title is the only wrapping element here, so its measured height is the whole layout.
            // Measured AFTER the layout runs: `preferredHeight` read straight after a text assignment can still be
            // last frame's, which would place the body under the wrong header.
            title.ForceMeshUpdate();
            head = Mathf.Max(22f, title.preferredHeight);
            body.rectTransform.anchoredPosition = new Vector2(Pad, -Pad - head - 2f);

            if (_chartRoot != null)
            {
                foreach (Transform child in _chartRoot.transform)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }

            if (categories.Values.Count == 0)
            {
                body.text = "";
                rect.sizeDelta = new Vector2(Width, head + Pad * 2 + 8f);
                return;
            }

            var maxCategoryTotal = categories.Values.Max(x => x.GetTotalDamage());
            var rowIndex = 0;
            var chartWidth = Width;
            var chartHeight = 0f;

            foreach (var c in categories.Values.OrderByDescending(x => x.GetTotalDamage()))
            {
                var ratio = maxCategoryTotal > 0d ? Math.Clamp(c.GetTotalDamage() / maxCategoryTotal, 0d, 1d) : 0d;
                var totalPercent = c.GetTotalDamage() / (damage ?? 1d);
                var valueText = $"{letterFormat((float)c.GetTotalDamage())} ({letterFormat((float)c.dps)}, {totalPercent:P0})";
                var labelText = c.Name;

                var row = new GameObject($"BarRow_{rowIndex++}");
                var rowRt = row.AddComponent<RectTransform>();
                rowRt.SetParent(_chartRoot != null ? _chartRoot.transform : null, false);
                rowRt.anchorMin = new Vector2(0f, 1f);
                rowRt.anchorMax = new Vector2(0f, 1f);
                rowRt.pivot = new Vector2(0f, 1f);
                rowRt.sizeDelta = new Vector2(chartWidth, 18f);
                rowRt.anchoredPosition = new Vector2(0f, -chartHeight);
                chartHeight += 20f;

                var rowTooltip = row.AddComponent<TooltipSource>();
                rowTooltip.Title = labelText;
                var breakdown = new StringBuilder();
                breakdown.AppendLine($"Total damage: {letterFormat((float)c.GetTotalDamage())}");
                breakdown.AppendLine($"DPS: {letterFormat((float)c.dps)}");
                breakdown.AppendLine($"Base damage: {letterFormat((float)c.BaseTotal)}");
                breakdown.AppendLine($"Hit count: {c.hitCount}");
                breakdown.AppendLine($"Crit count: {c.CritCount}");
                breakdown.AppendLine($"Crit percentage: {c.CritPercentage:P1}");
                breakdown.AppendLine($"Avg hit: {letterFormat((float)(c.hitCount > 0 ? c.BaseTotal / c.hitCount : 0d))}");
                breakdown.AppendLine($"Min hit: {letterFormat((float)(c.hitCount > 0 ? c.minHit : 0d))}");
                breakdown.AppendLine($"Max hit: {letterFormat((float)(c.hitCount > 0 ? c.maxHit : 0d))}");
                if (c.ExtraHeat > 0d) breakdown.AppendLine($"Extra Heat: {letterFormat((float)c.ExtraHeat)}");
                if (c.ExtraCold > 0d) breakdown.AppendLine($"Extra Cold: {letterFormat((float)c.ExtraCold)}");
                if (c.ExtraEnergy > 0d) breakdown.AppendLine($"Extra Energy: {letterFormat((float)c.ExtraEnergy)}");
                if (c.ExtraKinetic > 0d) breakdown.AppendLine($"Extra Kinetic: {letterFormat((float)c.ExtraKinetic)}");
                if (c.ExtraRadiation > 0d) breakdown.AppendLine($"Extra Radiation: {letterFormat((float)c.ExtraRadiation)}");
                if (c.ExtraCorrosion > 0d) breakdown.AppendLine($"Extra Corrosion: {letterFormat((float)c.ExtraCorrosion)}");
                if (c.ExtraExplosive > 0d) breakdown.AppendLine($"Extra Explosive: {letterFormat((float)c.ExtraExplosive)}");
                rowTooltip.BodyText = breakdown.ToString().TrimEnd();

                var rowMask = row.AddComponent<RectMask2D>();
                rowMask.padding = Vector4.zero;
                rowMask.softness = Vector2Int.zero;

                var rowBg = row.AddComponent<Image>();
                rowBg.sprite = CreateSolidSprite();
                rowBg.type = Image.Type.Simple;
                rowBg.color = new Color(0.12f, 0.13f, 0.15f, 0.12f);
                rowBg.raycastTarget = false;

                var hitBox = new GameObject("Hitbox");
                var hitBoxRt = hitBox.AddComponent<RectTransform>();
                hitBoxRt.SetParent(row.transform, false);
                hitBoxRt.anchorMin = Vector2.zero;
                hitBoxRt.anchorMax = Vector2.one;
                hitBoxRt.pivot = new Vector2(0.5f, 0.5f);
                hitBoxRt.anchoredPosition = Vector2.zero;
                hitBoxRt.sizeDelta = Vector2.zero;

                var hitImage = hitBox.AddComponent<Image>();
                hitImage.sprite = CreateSolidSprite();
                hitImage.type = Image.Type.Simple;
                hitImage.color = new Color(0f, 0f, 0f, 0f);
                hitImage.raycastTarget = true;
                hitBoxRt.SetAsLastSibling();

                var barWidth = chartWidth * (float)ratio;
                var segmentRoot = new GameObject("Segments");
                var segmentRootRt = segmentRoot.AddComponent<RectTransform>();
                segmentRootRt.SetParent(rowRt, false);
                segmentRootRt.anchorMin = new Vector2(0f, 0f);
                segmentRootRt.anchorMax = new Vector2(0f, 1f);
                segmentRootRt.pivot = new Vector2(0f, 0.5f);
                segmentRootRt.anchoredPosition = Vector2.zero;
                segmentRootRt.sizeDelta = new Vector2(barWidth, 18f);
                segmentRootRt.SetAsFirstSibling();

                var segmentDefs = new[]
                {
                    (Value: c.BaseTotal, Type: c.BaseDamageType),
                    (Value: c.ExtraHeat, Type: DamageType.Heat),
                    (Value: c.ExtraCold, Type: DamageType.Cold),
                    (Value: c.ExtraEnergy, Type: DamageType.Energy),
                    (Value: c.ExtraKinetic, Type: DamageType.Kinetic),
                    (Value: c.ExtraRadiation, Type: DamageType.Radiation),
                    (Value: c.ExtraCorrosion, Type: DamageType.Corrosion),
                    (Value: c.ExtraExplosive, Type: DamageType.Explosive)
                };

                var visibleSegments = segmentDefs.Where(x => x.Value > 0d).ToArray();
                var segmentTotal = visibleSegments.Sum(x => x.Value);
                var dividerColor = new Color(0.07f, 0.08f, 0.10f, 1f);
                var dividerWidth = 1.5f;
                var usableBarWidth = Math.Max(0f, barWidth - dividerWidth * Math.Max(0, visibleSegments.Length - 1));
                var runningX = 0f;
                for (var i = 0; i < visibleSegments.Length; i++)
                {
                    var segmentDef = visibleSegments[i];
                    var segmentWidth = segmentTotal > 0d ? (float)(segmentDef.Value / segmentTotal * usableBarWidth) : 0f;
                    if (segmentWidth <= 0f)
                    {
                        continue;
                    }

                    var segment = new GameObject($"Segment_{segmentDef.Type}");
                    var segmentRt = segment.AddComponent<RectTransform>();
                    segmentRt.SetParent(segmentRootRt, false);
                    segmentRt.anchorMin = new Vector2(0f, 0f);
                    segmentRt.anchorMax = new Vector2(0f, 1f);
                    segmentRt.pivot = new Vector2(0f, 0.5f);
                    segmentRt.anchoredPosition = new Vector2(runningX, 0f);
                    segmentRt.sizeDelta = new Vector2(segmentWidth, 18f);

                    var segmentImage = segment.AddComponent<Image>();
                    segmentImage.sprite = CreateSolidSprite();
                    segmentImage.type = Image.Type.Simple;
                    var baseColor = segmentDef.Type.GetColor();
                    var shadedColor = i == 0
                        ? new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a)
                        : new Color(Mathf.Clamp(baseColor.r * 0.82f, 0f, 1f), Mathf.Clamp(baseColor.g * 0.82f, 0f, 1f), Mathf.Clamp(baseColor.b * 0.82f, 0f, 1f), baseColor.a);
                    segmentImage.color = shadedColor;
                    segmentImage.raycastTarget = false;

                    runningX += segmentWidth;

                    if (i < visibleSegments.Length - 1)
                    {
                        var divider = new GameObject($"Divider_{segmentDef.Type}");
                        var dividerRt = divider.AddComponent<RectTransform>();
                        dividerRt.SetParent(segmentRootRt, false);
                        dividerRt.anchorMin = new Vector2(0f, 0f);
                        dividerRt.anchorMax = new Vector2(0f, 1f);
                        dividerRt.pivot = new Vector2(0f, 0.5f);
                        dividerRt.anchoredPosition = new Vector2(runningX, 0f);
                        dividerRt.sizeDelta = new Vector2(dividerWidth, 18f);

                        var dividerImage = divider.AddComponent<Image>();
                        dividerImage.sprite = CreateSolidSprite();
                        dividerImage.type = Image.Type.Simple;
                        dividerImage.color = dividerColor;
                        dividerImage.raycastTarget = false;

                        runningX += dividerWidth;
                    }
                }

                var label = CreateText(row.transform, labelText, new Vector2(6f, 0f), TextAlignmentOptions.Left, Color.white, 13f, 0f, 0f);
                label.rectTransform.anchorMin = new Vector2(0f, 0f);
                label.rectTransform.anchorMax = new Vector2(0f, 1f);
                label.rectTransform.pivot = new Vector2(0f, 0.5f);
                label.rectTransform.anchoredPosition = new Vector2(6f, 0f);
                label.rectTransform.sizeDelta = new Vector2(Mathf.Min(230f, chartWidth * 0.7f), 18f);
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Overflow;
                label.fontStyle = FontStyles.Bold;
                label.outlineColor = Color.black;
                label.outlineWidth = 0.35f;

                var value = CreateText(row.transform, valueText, new Vector2(-6f, 0f), TextAlignmentOptions.Right, new Color(0.55f, 0.92f, 0.60f), 13f, 1f, 1f);
                value.rectTransform.anchorMin = new Vector2(1f, 0f);
                value.rectTransform.anchorMax = new Vector2(1f, 1f);
                value.rectTransform.pivot = new Vector2(1f, 0.5f);
                value.rectTransform.anchoredPosition = new Vector2(-6f, 0f);
                value.rectTransform.sizeDelta = new Vector2(80f, 18f);
                value.textWrappingMode = TextWrappingModes.NoWrap;

                label.raycastTarget = false;
                value.raycastTarget = false;

                var labelSort = label.transform as RectTransform;
                if (labelSort != null)
                {
                    labelSort.SetAsLastSibling();
                }
            }

            if (_chartRoot != null)
            {
                var chartRt = _chartRoot.GetComponent<RectTransform>();
                chartRt.sizeDelta = new Vector2(chartWidth, chartHeight + 2f);
            }

            body.text = "";

            body.text = sb.ToString();

            // The panel is as tall as what it holds. A fixed height either clips the list or leaves a hole.
            var height = Mathf.Clamp(body.preferredHeight + head + 22f, 70f, 620f);
            rect.sizeDelta = new Vector2(Width, height);
        }

        private static Vector2? PlayerPosition()
        {
            try
            {
                var ship = Source.Player.GamePlayer.current?.currentSpaceShip;
                var unit = ship == null ? null : ship.unit;
                return unit == null ? (Vector2?)null : (Vector2)unit.transform.position;
            }
            catch { return null; }
        }

        // ---- construction -------------------------------------------------------------------------------

        private static bool Build()
        {
            try
            {
                TMP_Text? template = FindLabel();
                if (template == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Console.WriteLine("no game text label to clone a font from, so the panel cannot be "
                                              + "drawn. The settings tab still shows the same list.");
                    }
                    return false;
                }

                _root = new GameObject("VGParserModPanel");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // Above the game's HUD, which sits at 0: a readout the player asked for should not end up behind a
                // panel they did not.
                canvas.sortingOrder = 200;
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                _root.AddComponent<GraphicRaycaster>();

                var window = new GameObject("Window");
                window.transform.SetParent(_root.transform, false);
                _rect = window.AddComponent<RectTransform>();
                _rect.anchorMin = _rect.anchorMax = new Vector2(0f, 1f);   // top-left, so saved positions are stable
                _rect.pivot = new Vector2(0f, 1f);
                _rect.sizeDelta = new Vector2(Width, 120f);
                _rect.anchoredPosition = LoadPosition();

                var bg = window.AddComponent<Image>();
                bg.color = new Color(0.06f, 0.07f, 0.09f, 0.88f);
                bg.raycastTarget = true;                                    // the whole window is the drag handle
                window.AddComponent<Dragger>().Target = _rect;

                _title = Clone(template, window.transform, new Vector2(Pad, -Pad), Width - Pad * 2, 22f);
                _body = Clone(template, window.transform, new Vector2(Pad, -Pad - 24f), Width - Pad * 2, 0f);
                _body.textWrappingMode = TextWrappingModes.NoWrap;

                var toggleGo = new GameObject("ChartToggleButton");
                toggleGo.transform.SetParent(window.transform, false);
                var toggleRt = toggleGo.AddComponent<RectTransform>();
                toggleRt.anchorMin = new Vector2(1f, 1f);
                toggleRt.anchorMax = new Vector2(1f, 1f);
                toggleRt.pivot = new Vector2(1f, 1f);
                toggleRt.anchoredPosition = new Vector2(-Pad - 68f, -Pad);
                toggleRt.sizeDelta = new Vector2(22f, 22f);

                var toggleBg = toggleGo.AddComponent<Image>();
                toggleBg.sprite = CreateSolidSprite();
                toggleBg.color = new Color(0.18f, 0.19f, 0.22f, 0.95f);
                toggleBg.raycastTarget = true;

                _chartToggleButton = toggleGo.AddComponent<Button>();
                _chartToggleButton.targetGraphic = toggleBg;
                _chartToggleButton.transition = Selectable.Transition.ColorTint;
                _chartToggleButton.colors = new ColorBlock
                {
                    normalColor = new Color(0.18f, 0.19f, 0.22f, 0.95f),
                    highlightedColor = new Color(0.28f, 0.30f, 0.35f, 1f),
                    pressedColor = new Color(0.12f, 0.13f, 0.16f, 1f),
                    selectedColor = new Color(0.18f, 0.19f, 0.22f, 0.95f),
                    disabledColor = new Color(0.18f, 0.19f, 0.22f, 0.5f),
                    colorMultiplier = 1f,
                    fadeDuration = 0.05f
                };
                _chartToggleButton.onClick.AddListener(ToggleChartVisibility);

                _chartToggleLabel = CreateText(toggleGo.transform, "v", new Vector2(0f, 0f), TextAlignmentOptions.Center,
                    Color.white, 12f, 0.5f, 0.5f);
                _chartToggleLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
                _chartToggleLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
                _chartToggleLabel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                _chartToggleLabel.rectTransform.anchoredPosition = Vector2.zero;
                _chartToggleLabel.rectTransform.sizeDelta = new Vector2(0f, 0f);
                _chartToggleLabel.fontStyle = FontStyles.Bold;
                _chartToggleLabel.outlineColor = Color.black;
                _chartToggleLabel.outlineWidth = 0.2f;

                var clearGo = new GameObject("ClearButton");
                clearGo.transform.SetParent(window.transform, false);
                var clearRt = clearGo.AddComponent<RectTransform>();
                clearRt.anchorMin = new Vector2(1f, 1f);
                clearRt.anchorMax = new Vector2(1f, 1f);
                clearRt.pivot = new Vector2(1f, 1f);
                clearRt.anchoredPosition = new Vector2(-Pad, -Pad);
                clearRt.sizeDelta = new Vector2(54f, 22f);

                var clearBg = clearGo.AddComponent<Image>();
                clearBg.sprite = CreateSolidSprite();
                clearBg.color = new Color(0.18f, 0.19f, 0.22f, 0.95f);
                clearBg.raycastTarget = true;

                _clearButton = clearGo.AddComponent<Button>();
                _clearButton.targetGraphic = clearBg;
                _clearButton.transition = Selectable.Transition.ColorTint;
                _clearButton.colors = new ColorBlock
                {
                    normalColor = new Color(0.18f, 0.19f, 0.22f, 0.95f),
                    highlightedColor = new Color(0.28f, 0.30f, 0.35f, 1f),
                    pressedColor = new Color(0.12f, 0.13f, 0.16f, 1f),
                    selectedColor = new Color(0.18f, 0.19f, 0.22f, 0.95f),
                    disabledColor = new Color(0.18f, 0.19f, 0.22f, 0.5f),
                    colorMultiplier = 1f,
                    fadeDuration = 0.05f
                };
                _clearButton.onClick.AddListener(ClearCurrentDamageStats);

                var clearLabel = CreateText(clearGo.transform, "Clear", new Vector2(0f, 0f), TextAlignmentOptions.Center,
                    Color.white, 12f, 0.5f, 0.5f);
                clearLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
                clearLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
                clearLabel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                clearLabel.rectTransform.anchoredPosition = Vector2.zero;
                clearLabel.rectTransform.sizeDelta = new Vector2(0f, 0f);
                clearLabel.fontStyle = FontStyles.Bold;
                clearLabel.outlineColor = Color.black;
                clearLabel.outlineWidth = 0.2f;

                _chartRoot = new GameObject("Chart");
                var chartRt = _chartRoot.AddComponent<RectTransform>();
                chartRt.SetParent(window.transform, false);
                chartRt.anchorMin = new Vector2(0f, 1f);
                chartRt.anchorMax = new Vector2(1f, 1f);
                chartRt.pivot = new Vector2(0f, 1f);
                chartRt.anchoredPosition = new Vector2(0f, -Pad - 26f);
                chartRt.sizeDelta = new Vector2(Width, 0f);
                var chartMask = _chartRoot.AddComponent<RectMask2D>();
                chartMask.padding = Vector4.zero;
                chartMask.softness = Vector2Int.zero;

                _root.SetActive(false);
                Console.WriteLine("Panel built");
                return true;
            }
            catch (Exception e)
            {
               Console.WriteLine($"panel could not be built: {e.Message}");
                _root = null;
                return false;
            }
        }

        // The font comes from the GAME's own label, cloned: a mod that ships no font and picks no built-in one
        // cannot end up with the wrong glyphs or a missing material.
        private static TMP_Text? FindLabel()
        {
            var all = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None);
            foreach (var t in all)
                if (t != null && t.font != null) return t;
            return null;
        }

        private static TMP_Text Clone(TMP_Text template, Transform parent, Vector2 at, float width, float height)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = at;
            rt.sizeDelta = new Vector2(width, height);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = template.font;
            text.fontSharedMaterial = template.fontSharedMaterial;
            text.fontSize = 15f;
            text.color = new Color(0.91f, 0.93f, 0.95f);
            text.richText = true;
            text.raycastTarget = false;      // clicks belong to the window, so dragging works anywhere on it
            text.alignment = TextAlignmentOptions.TopLeft;
            text.text = "";
            return text;
        }

        private static TMP_Text CreateText(Transform parent, string text, Vector2 anchoredPosition, TextAlignmentOptions alignment,
            Color color, float fontSize, float anchorMinX, float anchorMaxX)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(anchorMinX, 0f);
            rt.anchorMax = new Vector2(anchorMaxX, 1f);
            rt.pivot = new Vector2(anchorMinX, 0.5f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = new Vector2(300f, 18f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = _title != null ? _title.font : null;
            tmp.fontSharedMaterial = _title != null ? _title.fontSharedMaterial : null;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.richText = true;
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            tmp.text = text;
            return tmp;
        }

        private static Sprite CreateSolidSprite()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        }

        //---- position, remembered ----------------------------------------------------------------------

        private static Vector2 LoadPosition()
        {
            try
            {
                var raw = Plugin.PanelPositionSetting.Value;
                if (!string.IsNullOrEmpty(raw))
                {
                    var parts = raw.Split(',');
                    if (parts.Length >= 2
                        && float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y))
                        return new Vector2(x, y);
                }
            }
            catch { }
            return new Vector2(24f, -120f);
        }

        internal static void SavePosition()
        {
            try
            {
                if (_rect == null || Plugin.PanelPositionSetting == null) return;
                var p = _rect.anchoredPosition;
                Plugin.PanelPositionSetting.Value = $"{p.x:0},{p.y:0}";
            }
            catch { }
        }
    }

    // Drag on Unity's own interface, NOT the game's DraggableWindowBar: that one wants an IDraggableWindow
    // implementation (a compiled game typeref this mod must not take) plus prefab-serialised colours and child
    // handles, so attaching it to a mod-built object yields null wiring and an exception inside game code.
    internal sealed class Dragger : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal RectTransform? Target;

        public void OnBeginDrag(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            var target = Target;
            if (target == null) return;
            target.anchoredPosition += e.delta;
        }

        // Written on release rather than on every frame of the drag: the config file is on disk.
        public void OnEndDrag(PointerEventData e)
        {
            Panel.SavePosition();
        }
    }
}