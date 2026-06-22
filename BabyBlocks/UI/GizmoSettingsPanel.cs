using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Panels;

namespace BabyBlocks.UI
{
    class GizmoSettingsPanel : PanelBase
    {
        public static GizmoSettingsPanel Instance { get; private set; }

        public override string  Name             => "Gizmo Settings";
        public override int     MinWidth         => 260;
        public override int     MinHeight        => 110;
        public override bool    CanDragAndResize => true;
        public override Vector2 DefaultAnchorMin => new(0f, 1f);
        public override Vector2 DefaultAnchorMax => new(0f, 1f);

        Slider _thicknessSlider;
        Text   _thicknessLabel;
        Slider _occAlphaSlider;
        Text   _occAlphaLabel;

        public GizmoSettingsPanel(UIBase owner) : base(owner)
        {
            Instance = this;
        }

        public override void SetDefaultSizeAndPosition()
        {
            base.SetDefaultSizeAndPosition();
            Rect.anchoredPosition = new Vector2(8f, -8f);
            Rect.sizeDelta        = new Vector2(260f, 118f);
        }

        protected override void ConstructPanelContent()
        {
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 4, padTop: 6, padBottom: 6, padLeft: 8, padRight: 8);

            BuildSliderRow("Outline width:",  GizmoRenderer.OutlineThickness,    "F3",
                0.005f, 0.15f, out _thicknessSlider, out _thicknessLabel, OnThicknessChanged);
            BuildSliderRow("Occluded alpha:", GizmoRenderer.OutlineOccludedAlpha, "F2",
                0f,     1f,    out _occAlphaSlider,  out _occAlphaLabel,  OnOccAlphaChanged);
        }

        void BuildSliderRow(string label, float initial, string fmt,
            float min, float max, out Slider slider, out Text valueLabel,
            System.Action<float> onChange)
        {
            var row = UIFactory.CreateHorizontalGroup(ContentRoot, label + "Row",
                false, false, true, true, spacing: 6);
            UIFactory.SetLayoutElement(row, minHeight: 20, flexibleWidth: 9999);

            var lbl = UIFactory.CreateLabel(row, label + "Lbl", label,
                TextAnchor.MiddleLeft, fontSize: 13);
            UIFactory.SetLayoutElement(lbl.gameObject, minWidth: 110, flexibleWidth: 0);

            valueLabel = UIFactory.CreateLabel(row, label + "Val", initial.ToString(fmt),
                TextAnchor.MiddleRight, fontSize: 13);
            UIFactory.SetLayoutElement(valueLabel.gameObject, minWidth: 36, flexibleWidth: 0);

            var sliderObj = UIFactory.CreateSlider(ContentRoot, label + "Slider", out slider);
            UIFactory.SetLayoutElement(sliderObj, minHeight: 20, flexibleWidth: 9999);
            slider.minValue = min;
            slider.maxValue = max;
            slider.value    = initial;
            slider.onValueChanged.AddListener((System.Action<float>)onChange);
        }

        void OnThicknessChanged(float value)
        {
            GizmoRenderer.OutlineThickness = value;
            if (_thicknessLabel != null) _thicknessLabel.text = value.ToString("F3");
        }

        void OnOccAlphaChanged(float value)
        {
            GizmoRenderer.OutlineOccludedAlpha = value;
            if (_occAlphaLabel != null) _occAlphaLabel.text = value.ToString("F2");
        }
    }
}
