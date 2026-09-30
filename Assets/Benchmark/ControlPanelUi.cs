using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AnimatorLodTest
{
    /// <summary>
    /// 検証シーン共通の右上 uGUI パネル生成ヘルパー。
    /// LodControlPanel(AnimatorLodTest)の見た目・並び(共通部 → シーン固有の最適化 → Rig / Move)を組むために使う。
    /// </summary>
    public static class ControlPanelUi
    {
        public const float ButtonHeight = 30f;
        private static readonly Color PanelColor = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ButtonColor = new Color(0.22f, 0.22f, 0.22f, 0.9f);

        /// <summary>新 Input System 専用設定のため EventSystem には InputSystemUIInputModule を使う。</summary>
        public static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetSiblingIndex(0);
        }

        /// <summary>Canvas と右上パネル(縦並び・内容に合わせて高さ自動)を作る。</summary>
        public static RectTransform CreatePanel(Transform owner)
        {
            var canvasGo = new GameObject("ControlPanelCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(owner, false);

            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            var panel = CreateRect("Panel", canvasGo.transform);
            panel.gameObject.AddComponent<Image>().color = PanelColor;
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-10f, -10f);
            panel.sizeDelta = new Vector2(280f, 0f);

            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return panel;
        }

        /// <summary>パネル先頭の体数ラベル。</summary>
        public static Text CreateHeader(RectTransform parent, Font font, string content)
        {
            var text = CreateText(parent, font, content, 16, TextAnchor.MiddleCenter);
            AddLayoutHeight(text.gameObject, 24f);
            return text;
        }

        public static Text CreateButton(RectTransform parent, Font font, string label, UnityEngine.Events.UnityAction onClick)
        {
            var rect = CreateBox("Button_" + label, parent, out var image);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            return CreateFillText(rect, font, label);
        }

        /// <summary>ボタンと同じ下敷き(背景)を持つ、クリック不可のラベル。</summary>
        public static Text CreateLabelBox(RectTransform parent, Font font, string label)
        {
            var rect = CreateBox("Label_" + label, parent, out var image);
            image.raycastTarget = false;
            return CreateFillText(rect, font, label);
        }

        /// <summary>セクション区切り(小さな余白)。</summary>
        public static void CreateSpacer(RectTransform parent)
        {
            AddLayoutHeight(CreateRect("Spacer", parent).gameObject, 4f);
        }

        public static string OnOff(bool value) => value ? "ON" : "OFF";

        // ---- 内部 ----

        private static RectTransform CreateBox(string name, RectTransform parent, out Image image)
        {
            var rect = CreateRect(name, parent);
            AddLayoutHeight(rect.gameObject, ButtonHeight);
            image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            return rect;
        }

        private static Text CreateFillText(RectTransform parent, Font font, string label)
        {
            var text = CreateText(parent, font, label, 15, TextAnchor.MiddleCenter);
            var textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            return text;
        }

        private static Text CreateText(RectTransform parent, Font font, string content, int fontSize, TextAnchor anchor)
        {
            var rect = CreateRect("Text", parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = Color.white;
            // ボタン幅に収まらないラベル(例: "Culling: CullUpdateTransforms")を折り返さない
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = content;
            return text;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void AddLayoutHeight(GameObject go, float height)
        {
            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
        }
    }
}
