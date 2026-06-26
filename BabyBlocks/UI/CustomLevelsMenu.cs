using BabyStepsMenuLib;
using Il2CppTMPro;
using MelonLoader.Utils;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BabyBlocks.UI
{
    static class CustomLevelsMenu
    {
        private static MenuInjectionLibrary.InjectedMenu _menu;

        private static readonly List<string> _levelFiles = new();
        private static readonly List<Button> _levelButtons = new();
        private static Button _prevPageBtn;
        private static TMP_Text _pageLabel;
        private static Button _nextPageBtn;
        private const int PageSize = 9;
        private static int _page;
        private static bool _buttonsReady;
        private static float _lastLoadClickTime = -10f;
        private static float _lastPageClickTime = -10f;

        static string LevelsFolder =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "levels");

        public static void Initialize()
        {
            if (_menu != null) return;

            _menu = MenuInjectionLibrary.CreateMenu("Custom Levels")
                .AddTab("Levels", ConfigureLevelsTab)
                .AddFixedButton("Back")
                .WithMargin(136f, 125f)
                .Build();
        }

        public static void Update()
        {
            if (_buttonsReady || _levelButtons.Count == 0) return;

            _buttonsReady = true;
            RepositionPaginationRow();

            var backBtn = _menu?.GetFixedButton(0);
            if (backBtn != null)
            {
                var rt = backBtn.GetComponent<RectTransform>();
                if (rt != null)
                    rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y - 135f);
            }

            RebuildLevelUI();
        }

        static void ConfigureLevelsTab(MenuInjectionLibrary.TabBuilder tab)
        {
            tab.AddImage(Color.clear, 10f);

            _prevPageBtn = tab.AddButton("<", (UnityAction)OnPrevPage);
            _pageLabel = tab.AddLabel("1/1");
            _nextPageBtn = tab.AddButton(">", (UnityAction)OnNextPage);

            for (int i = 0; i < PageSize; i++)
            {
                int captured = i;
                var btn = tab.AddButton("---", (UnityAction)(() => OnLevelClicked(captured)));
                btn.interactable = false;
                var cb = btn.colors;
                cb.disabledColor = new Color(cb.disabledColor.r, cb.disabledColor.g, cb.disabledColor.b, 0.8f);
                btn.colors = cb;
                _levelButtons.Add(btn);
            }
        }

        static void RebuildLevelUI()
        {
            _levelFiles.Clear();
            try
            {
                string folder = LevelsFolder;
                if (Directory.Exists(folder))
                {
                    var files = Directory.GetFiles(folder, "*.bbb", SearchOption.TopDirectoryOnly);
                    _levelFiles.AddRange(files);
                    _levelFiles.Sort();
                }
            }
            catch { }

            int pageCount = Mathf.Max(1, Mathf.CeilToInt(_levelFiles.Count / (float)PageSize));
            _page = Mathf.Clamp(_page, 0, pageCount - 1);
            int startIdx = _page * PageSize;

            if (_pageLabel != null)
            {
                _pageLabel.text = $"{_page + 1}/{pageCount}";
                _pageLabel.ForceMeshUpdate();
            }

            for (int i = 0; i < _levelButtons.Count; i++)
            {
                int fileIdx = startIdx + i;
                var btn = _levelButtons[i];
                if (btn == null) continue;

                if (fileIdx < _levelFiles.Count)
                {
                    SetBtnText(btn, Path.GetFileNameWithoutExtension(_levelFiles[fileIdx]));
                    btn.interactable = true;
                }
                else
                {
                    SetBtnText(btn, "---");
                    btn.interactable = false;
                }
            }
        }

        static void OnPrevPage()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastPageClickTime < 0.3f) return;
            _lastPageClickTime = now;
            int pages = Mathf.Max(1, Mathf.CeilToInt(_levelFiles.Count / (float)PageSize));
            _page = (_page - 1 + pages) % pages;
            RebuildLevelUI();
        }

        static void OnNextPage()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastPageClickTime < 0.3f) return;
            _lastPageClickTime = now;
            int pages = Mathf.Max(1, Mathf.CeilToInt(_levelFiles.Count / (float)PageSize));
            _page = (_page + 1) % pages;
            RebuildLevelUI();
        }

        static void OnLevelClicked(int slot)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastLoadClickTime < 0.5f) return;
            _lastLoadClickTime = now;

            int index = _page * PageSize + slot;
            if (index >= _levelFiles.Count) return;

            string path = _levelFiles[index];
            LevelEditor.EnsureManager();
            GpuiPropScanner.ScanGpuiProps();
            MaterialCatalog.InvalidateMaterialSourcesSync();
            var (ok, _, _) = LevelSaveLoad.Load(path);
            if (ok)
            {
                MaterialVariantTracker.InvalidateMaterialCache();
                MaterialCatalog.ReapplyAllMaterialOverrides();
                Core.LastSavePath = path;
                Networking.ModNetworking.BroadcastLevelLoad();
                // GPUI assigns textures to prop materials asynchronously after placement.
                // Retry reapply so hash-variant materials (e.g. "New Material [70608036]")
                // are findable once GPUI has loaded them into CPU memory.
                MelonLoader.MelonCoroutines.Start(DeferredMaterialReapplyCo());
            }
        }

        static System.Collections.IEnumerator DeferredMaterialReapplyCo()
        {
            float[] delays = { 2f, 4f };
            foreach (float delay in delays)
            {
                float until = UnityEngine.Time.realtimeSinceStartup + delay;
                while (UnityEngine.Time.realtimeSinceStartup < until) yield return null;
                MaterialVariantTracker.InvalidateMaterialCache();
                MaterialCatalog.ReapplyAllMaterialOverrides();
            }
        }

        static void RepositionPaginationRow()
        {
            if (_prevPageBtn == null || _pageLabel == null || _nextPageBtn == null) return;

            var prevRT = _prevPageBtn.GetComponent<RectTransform>();
            var labelRT = _pageLabel.GetComponent<RectTransform>();
            var nextRT = _nextPageBtn.GetComponent<RectTransform>();
            if (prevRT == null || labelRT == null || nextRT == null) return;

            var parentRT = prevRT.parent as RectTransform;
            float pageW = parentRT != null && parentRT.rect.width > 10f ? parentRT.rect.width : 800f;

            float rowY = prevRT.anchoredPosition.y;
            const float rowH = 52f;
            const float arrowW = 90f;
            float labelW = pageW - arrowW * 2f;
            const float shift = 2f * (rowH + 10f);

            void Place(RectTransform rt, float x, float w)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(w, rowH);
                rt.anchoredPosition = new Vector2(x, rowY);
            }

            Place(prevRT,  0f,              arrowW);
            Place(labelRT, arrowW,          labelW);
            Place(nextRT,  arrowW + labelW, arrowW);

            var prevTmp = _prevPageBtn.GetComponentInChildren<TMP_Text>(true);
            if (prevTmp != null) { prevTmp.fontStyle = FontStyles.Bold; prevTmp.ForceMeshUpdate(); }

            var nextTmp = _nextPageBtn.GetComponentInChildren<TMP_Text>(true);
            if (nextTmp != null) { nextTmp.fontStyle = FontStyles.Bold; nextTmp.ForceMeshUpdate(); }

            _pageLabel.alignment = TextAlignmentOptions.Center;
            _pageLabel.fontSize = 20f;
            _pageLabel.ForceMeshUpdate();

            foreach (var btn in _levelButtons)
            {
                var rt = btn?.GetComponent<RectTransform>();
                if (rt != null)
                    rt.anchoredPosition += new Vector2(0f, shift);
            }
        }

        static void SetBtnText(Button btn, string text)
        {
            if (btn == null) return;
            var tmp = btn.GetComponentInChildren<TMP_Text>(true);
            if (tmp == null) return;
            tmp.text = text;
            tmp.ForceMeshUpdate();
        }
    }
}
