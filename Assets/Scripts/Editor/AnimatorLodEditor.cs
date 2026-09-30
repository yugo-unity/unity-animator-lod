using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AnimatorLodTest.Editor
{
    /// <summary>
    /// AnimatorLod の Inspector。
    /// References(表示のみ)/ LOD の 2 バンド。LOD バンドは共通の LOD 境界
    /// (Ratios バー・Transition・Culled)と、それを使う Animation LOD / Mesh LOD / Skin Weights のサブバンドで構成する。
    /// 各項目の詳細は README.md を参照。
    ///
    /// IMGUI はイベントごと(Layout / Repaint / 入力)に全体を描き直すため、固定のラベル・スタイル・SerializedProperty・
    /// デリゲートはキャッシュし、描画のたびに確保しない。
    /// </summary>
    [CustomEditor(typeof(AnimatorLod))]
    [CanEditMultipleObjects]
    public sealed class AnimatorLodEditor : UnityEditor.Editor
    {
        private const float MinPercent = 0.01f;
        private const float BarHeight = 34f;
        private const float BarLabelMinWidth = 30f;

        // 折りたたみ状態は Editor セッション内で記憶
        private const string ReferencesKey = "AnimatorLod.References";
        private const string ReferencesListKey = ReferencesKey + ".SMRs";
        private const string LodGroupKey = "AnimatorLod.LOD";
        private const string LodKey = "AnimatorLod.AnimationLOD";
        private const string MeshLodKey = "AnimatorLod.MeshLOD";
        private const string SkinWeightsKey = "AnimatorLod.DynamicSkinWeights";

        // ---- 固定ラベル ----
        private static readonly GUIContent AnimatorLabel = new GUIContent("Animator", "Animator on this GameObject. Read-only; fetched when the component is attached and by Refresh References.");
        private static readonly GUIContent RenderersLabel = new GUIContent("Skinned Mesh Renderers", "All child SkinnedMeshRenderers, including inactive ones. Read-only; fetched when the component is attached and by Refresh References.");
        private static readonly GUIContent RefreshReferencesLabel = new GUIContent("Refresh References", "Fetch the Animator and child SkinnedMeshRenderers again.");
        private static readonly GUIContent BoundsLabel = new GUIContent("Bounds", "AABB in this transform's local space. Used as the bounding sphere for LOD.");
        private static readonly GUIContent RatiosLabel = new GUIContent("Ratios", "Screen-size ratio per LOD (left 100%, right 0%). Drag the boundaries to edit.");
        private static readonly GUIContent CulledLabel = new GUIContent("Culled (% Screen Size)", "Below this ratio the Invisible Interval applies. 0 = off.");
        private static readonly GUIContent AnimationLodEnabledLabel = new GUIContent("Enabled", "Throttle the Animator update per LOD. Mesh LOD and Skin Weights work without this.");
        private static readonly GUIContent LodCameraLabel = new GUIContent("LOD Camera", "Camera for the screen-size ratio. Empty = Camera.main.");
        private static readonly GUIContent BaseIntervalLabel = new GUIContent("Base Interval", "Animator update interval (frames) added to every LOD. 0/1 = every frame.");
        private static readonly GUIContent InvisibleIntervalLabel = new GUIContent("Invisible Interval", "Interval used when not visible or culled. Overrides LOD and Base.");
        private static readonly GUIContent MeshLodEnabledLabel = new GUIContent("Enabled", "Swap SkinnedMeshRenderer.sharedMesh to a reduced mesh per LOD.");
        private static readonly GUIContent SkinWeightsEnabledLabel = new GUIContent("Enabled", "Switch SkinnedMeshRenderer.quality (bones per vertex) per LOD.");
        private static readonly GUIContent LodEvaluationIntervalLabel = new GUIContent("LOD Evaluation Interval",
            "Evaluate LOD for Mesh LOD and Skin Weights every N frames (spread across instances). 1 = every frame. " +
            "Not used while Animation LOD is enabled: LOD is then evaluated on the frames the Animator is updated.");
        private static readonly GUIContent EmptyListLabel = new GUIContent("(empty)");

        private static readonly GUIContent CalculateFromClipsLabel = new GUIContent("Calculate from All Clips",
            "Sample every clip of the Animator Controller and measure Bounds that contain every pose.");
        private static readonly GUIContent CalculateFromDefaultPoseLabel = new GUIContent("Calculate from Default Pose",
            "Measure Bounds from the default pose (no animation applied).");

        private const string StatusMixed = "(mixed)";
        private const string StatusAllClips = "Calculated from all clips";
        private const string StatusDefaultPose = "Calculated from default pose";
        private const string StatusRenderers = "Taken from renderer bounds";
        private static readonly GUIContent s_intervalRowLabel = new GUIContent(string.Empty, "Interval (frames) added to Base Interval for this LOD.");

        // ---- 番号付きラベル(index ごとに 1 度だけ作る) ----
        private static readonly List<GUIContent> s_elementLabels = new List<GUIContent>();
        private static readonly List<GUIContent> s_lodLabels = new List<GUIContent>();
        private static readonly List<GUIContent> s_skinLodLabels = new List<GUIContent>();
        private static readonly List<GUIContent> s_transitionLabels = new List<GUIContent>();
        private static readonly List<string> s_segmentNames = new List<string>();

        // ---- スタイル(EditorStyles は OnGUI 中にしか参照できないため遅延生成) ----
        private static GUIStyle s_sceneLabel;
        private static GUIStyle s_segmentLabel;
        private static GUIStyle s_readoutLabel;
        private static GUIStyle s_culledLabel;

        // ---- SerializedProperty / デリゲート(OnEnable で 1 度だけ取得) ----
        private SerializedProperty _animator;
        private SerializedProperty _renderers;
        private SerializedProperty _lodBounds;
        private SerializedProperty _boundsSource;
        private SerializedProperty _lodEnabled;
        private SerializedProperty _lodCamera;
        private SerializedProperty _lodRatios;
        private SerializedProperty _baseInterval;
        private SerializedProperty _invisibleInterval;
        private SerializedProperty _lodIntervals;
        private SerializedProperty _cullRatio;
        private SerializedProperty _meshLodEnabled;
        private SerializedProperty _lodEvaluationInterval;
        private SerializedProperty _skinWeightsLodEnabled;
        private SerializedProperty _lodSkinQualities;

        private System.Action _drawReferences;
        private System.Action _drawLodGroup;
        private System.Action _drawAnimationLod;
        private System.Action _drawMeshLod;
        private System.Action _drawSkinWeights;

        private void OnEnable()
        {
            _animator = serializedObject.FindProperty("animator");
            _renderers = serializedObject.FindProperty("renderers");
            _lodBounds = serializedObject.FindProperty("lodBounds");
            _boundsSource = serializedObject.FindProperty("boundsSource");
            _lodEnabled = serializedObject.FindProperty("lodEnabled");
            _lodCamera = serializedObject.FindProperty("lodCamera");
            _lodRatios = serializedObject.FindProperty("lodRatios");
            _baseInterval = serializedObject.FindProperty("baseInterval");
            _invisibleInterval = serializedObject.FindProperty("invisibleInterval");
            _lodIntervals = serializedObject.FindProperty("lodIntervals");
            _cullRatio = serializedObject.FindProperty("cullRatio");
            _meshLodEnabled = serializedObject.FindProperty("meshLodEnabled");
            _lodEvaluationInterval = serializedObject.FindProperty("lodEvaluationInterval");
            _skinWeightsLodEnabled = serializedObject.FindProperty("skinWeightsLodEnabled");
            _lodSkinQualities = serializedObject.FindProperty("lodSkinQualities");

            _drawReferences = DrawReferencesSection;
            _drawLodGroup = DrawLodGroup;
            _drawAnimationLod = DrawLodSection;
            _drawMeshLod = DrawMeshLodSection;
            _drawSkinWeights = DrawSkinWeightsSection;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawBand(ReferencesKey, "References", false, _drawReferences);
            DrawBand(LodGroupKey, "LOD", true, _drawLodGroup);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>LOD バンド: Bounds → 共通の LOD 境界 → Animation LOD / Mesh LOD / Skin Weights。</summary>
        private void DrawLodGroup()
        {
            DrawBoundsSection();

            if (serializedObject.isEditingMultipleObjects && !LaddersAgree(_lodRatios))
            {
                EditorGUILayout.HelpBox("Selected components have different ladders. Select one to edit.", MessageType.Info);
                return;
            }

            DrawLadder();
            DrawBand(LodKey, "Animation LOD", true, _drawAnimationLod);
            DrawBand(MeshLodKey, "Mesh LOD", true, _drawMeshLod);
            DrawBand(SkinWeightsKey, "Skin Weights", true, _drawSkinWeights);
        }

        /// <summary>Ratios バー、LOD n Transition、Culled、Level の増減。</summary>
        private void DrawLadder()
        {
            var ratios = _lodRatios;
            var cull = _cullRatio;
            var intervals = _lodIntervals;
            int boundaryCount = ratios.arraySize;
            // 境界のクランプ用にイベント開始時点の値を控える(バーと各行で共用)
            var current = Snapshot(ratios);

            // LOD Camera は Animation LOD / Mesh LOD / Skin Weights で共通の LOD 判定に使う
            EditorGUILayout.PropertyField(_lodCamera, LodCameraLabel);
            // Mesh LOD / Skin Weights 用の判定間隔。Animation LOD が有効な間(評価フレームで判定する)と、
            // Mesh LOD / Skin Weights がともに無効な間は参照されないのでグレーアウトする
            using (new EditorGUI.DisabledScope(IsAllOn(_lodEnabled) || (IsAllOff(_meshLodEnabled) && IsAllOff(_skinWeightsLodEnabled))))
            {
                EditorGUILayout.IntSlider(_lodEvaluationInterval, 1, AnimatorLod.MaxLodEvaluationInterval, LodEvaluationIntervalLabel);
            }

            Rect barRect = EditorGUILayout.GetControlRect(true, BarHeight);
            barRect = EditorGUI.PrefixLabel(barRect, RatiosLabel);
            DrawRatioBar(barRect, ratios, cull, current, boundaryCount);
            EditorGUILayout.Space(2f);

            for (int level = 1; level <= boundaryCount; level++)
            {
                DrawTransitionRow(ratios, cull, current, level - 1, boundaryCount);
            }

            EditorGUI.BeginChangeCheck();
            float culledPercent = EditorGUILayout.Slider(CulledLabel, RatioToPercent(cull.floatValue), 0f, 100f);
            if (EditorGUI.EndChangeCheck())
            {
                cull.floatValue = ClampCull(current, boundaryCount, PercentToRatio(culledPercent));
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(boundaryCount <= 1))
                {
                    if (GUILayout.Button("- Level", GUILayout.Width(80f)))
                    {
                        ratios.arraySize = boundaryCount - 1;
                        intervals.arraySize = boundaryCount - 1;
                        cull.floatValue = ClampCull(Snapshot(ratios), boundaryCount - 1, cull.floatValue);
                    }
                }
                if (GUILayout.Button("+ Level", GUILayout.Width(80f)))
                {
                    float last = boundaryCount > 0 ? current[boundaryCount - 1] : 1f;
                    ratios.arraySize = boundaryCount + 1;
                    float added = Mathf.Max(last * 0.5f, AnimatorLod.MinRatio);
                    ratios.GetArrayElementAtIndex(boundaryCount).floatValue = added;
                    intervals.arraySize = boundaryCount + 1;
                    int prevInterval = boundaryCount > 0
                        ? intervals.GetArrayElementAtIndex(boundaryCount - 1).intValue
                        : 0;
                    intervals.GetArrayElementAtIndex(boundaryCount).intValue =
                        Mathf.Min(AnimatorLod.MaxInterval, prevInterval + 1);
                    if (cull.floatValue > added)
                    {
                        cull.floatValue = added;
                    }
                }
            }
        }

        /// <summary>
        /// サブバンドの Enabled を描き、無効なら以降の項目をグレーアウトする。
        /// 複数選択で値が混在しているときはグレーアウトしない(一部は有効なため)。
        /// </summary>
        private static EditorGUI.DisabledScope DrawEnabledToggle(SerializedProperty enabled, GUIContent label)
        {
            EditorGUILayout.PropertyField(enabled, label);
            return new EditorGUI.DisabledScope(IsAllOff(enabled));
        }

        /// <summary>選択中の全コンポーネントで false か(値が混在していれば false)。</summary>
        private static bool IsAllOff(SerializedProperty flag) => !flag.hasMultipleDifferentValues && !flag.boolValue;

        /// <summary>選択中の全コンポーネントで true か(値が混在していれば false)。</summary>
        private static bool IsAllOn(SerializedProperty flag) => !flag.hasMultipleDifferentValues && flag.boolValue;

        // ---- Scene View: 選択中個体の LOD 表示 ----

        /// <summary>
        /// Hierarchy で選択中の AnimatorLod の頭上に現在の LOD を表示する(複数選択時は各個体に呼ばれる)。
        /// Play 中: AnimatorLodSystem が判定した値(Interval / 可視 / Mesh / Skin Weights も併記)。
        /// 編集中: ランタイムと同じカメラ(LOD Camera → Camera.main、無ければ Scene カメラ)で同じ式のプレビュー。
        /// 入力を扱わない表示のみなので Repaint イベントだけで描く(Layout や入力イベントで文字列を作らない)。
        /// </summary>
        private void OnSceneGUI()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var lod = (AnimatorLod)target;
            if (lod == null)
            {
                return;
            }

            // Bounds は設定値なので、コンポーネントが無効でも表示する
            DrawLodBounds(lod);

            if (!lod.isActiveAndEnabled)
            {
                return;
            }

            if (s_sceneLabel == null)
            {
                s_sceneLabel = new GUIStyle(EditorStyles.helpBox)
                {
                    fontSize = 12,
                    richText = true,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = Color.white },
                };
            }

            int levels = lod.LodLevelCount;
            int level;
            float ratio;
            string source;
            string detail = null;

            if (Application.isPlaying && (lod.LodEnabled || lod.MeshLodEnabled || lod.SkinWeightsLodEnabled))
            {
                level = lod.CurrentLod;
                ratio = lod.CurrentScreenRatio;
                source = "runtime";
                var smr = FirstRenderer(lod);
                detail = lod.LodEnabled
                    ? $"Interval {lod.CurrentInterval}  Bucket {lod.CurrentBucket}  {(lod.IsInvisible ? "Invisible" : "Visible")}"
                    : "Animation LOD off (every frame)";
                if (smr != null && (lod.MeshLodEnabled || lod.SkinWeightsLodEnabled))
                {
                    int verts = smr.sharedMesh != null ? smr.sharedMesh.vertexCount : 0;
                    detail += $"\nMesh {verts} verts  Skin {smr.quality}";
                }
            }
            else if (Application.isPlaying)
            {
                level = -1;
                ratio = 0f;
                source = "runtime";
            }
            else
            {
                var cam = lod.LodCamera != null ? lod.LodCamera : Camera.main;
                source = cam != null ? cam.name : null;
                if (cam == null && SceneView.currentDrawingSceneView != null)
                {
                    cam = SceneView.currentDrawingSceneView.camera;
                    source = "Scene camera";
                }
                if (cam == null)
                {
                    return;
                }
                level = lod.EditorPreviewLod(cam, out ratio, out _);
            }

            string title = level < 0
                ? "LOD off"
                : level >= levels ? "Culled" : $"LOD {level}";
            string text = level < 0
                ? $"<b>{title}</b>"
                : $"<b>{title}</b>  {ratio * 100f:0.##}%  <color=#bbbbbb>({source})</color>";
            if (detail != null)
            {
                text += "\n" + detail;
            }

            // バウンディングの上端に表示
            var b = lod.LodBounds;
            var top = lod.transform.TransformPoint(b.center + Vector3.up * b.extents.y);

            Handles.BeginGUI();
            var content = new GUIContent(text);
            Vector2 size = s_sceneLabel.CalcSize(content);
            Vector2 gui = HandleUtility.WorldToGUIPoint(top);
            var rect = new Rect(gui.x - size.x * 0.5f, gui.y - size.y - 4f, size.x, size.y);

            var prev = GUI.backgroundColor;
            GUI.backgroundColor = level < 0 ? Color.gray
                : level >= levels ? new Color(0.25f, 0.25f, 0.25f)
                : SegmentColor(level, levels - 1) * 1.6f;
            GUI.Label(rect, content, s_sceneLabel);
            GUI.backgroundColor = prev;
            Handles.EndGUI();

            // Play 中は値が毎フレーム変わるので、選択中だけ Scene View を再描画し続ける
            // (UnityEngine.Object は ?. だと破棄済みを null と判定しないため明示的に比較する)
            var sceneView = SceneView.currentDrawingSceneView;
            if (Application.isPlaying && sceneView != null)
            {
                sceneView.Repaint();
            }
        }

        private static readonly Color BoundsBoxColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        private static readonly Color BoundsSphereColor = new Color(0.3f, 0.85f, 1f, 0.6f);
        private static readonly Color BoundsOutlineColor = new Color(0.3f, 0.85f, 1f, 1f);

        /// <summary>
        /// LOD 判定に使う Bounds を描く。AABB(このコンポーネントのローカル空間)と、
        /// 実際に Screen Size 比の計算に使う外接球(3 軸の円 + 視線に垂直な輪郭円)。
        /// </summary>
        private static void DrawLodBounds(AnimatorLod lod)
        {
            var b = lod.LodBounds;
            var prevMatrix = Handles.matrix;
            var prevColor = Handles.color;

            Handles.matrix = lod.transform.localToWorldMatrix;
            Handles.color = BoundsBoxColor;
            Handles.DrawWireCube(b.center, b.size);

            Handles.matrix = Matrix4x4.identity;
            lod.EditorGetLodSphere(out var center, out float radius);
            Handles.color = BoundsSphereColor;
            Handles.DrawWireDisc(center, Vector3.right, radius);
            Handles.DrawWireDisc(center, Vector3.up, radius);
            Handles.DrawWireDisc(center, Vector3.forward, radius);

            var cam = Camera.current;
            if (cam != null)
            {
                var ct = cam.transform;
                var normal = cam.orthographic ? ct.forward : center - ct.position;
                if (normal.sqrMagnitude > 1e-8f)
                {
                    Handles.color = BoundsOutlineColor;
                    Handles.DrawWireDisc(center, normal, radius, 2f);
                }
            }

            Handles.matrix = prevMatrix;
            Handles.color = prevColor;
        }

        /// <summary>References の最初の有効な SMR(Scene View の Mesh / Skin Weights 表示用)。</summary>
        private static SkinnedMeshRenderer FirstRenderer(AnimatorLod lod)
        {
            for (int i = 0; i < lod.EditorRendererCount; i++)
            {
                var smr = lod.EditorGetRenderer(i);
                if (smr != null)
                {
                    return smr;
                }
            }
            return null;
        }

        // ---- Mesh LOD ----

        private void DrawMeshLodSection()
        {
            using var _ = DrawEnabledToggle(_meshLodEnabled, MeshLodEnabledLabel);
            DrawMeshLodLevels();

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Generate Reduced Meshes"))
                {
                    foreach (var t in targets)
                    {
                        var lod = (AnimatorLod)t;
                        if (AnimatorLodMeshReducer.Generate(lod, out string report, false))
                        {
                            Debug.Log($"[AnimatorLod] {lod.name}: reduced meshes generated\n{report}", lod);
                        }
                        else
                        {
                            Debug.LogWarning($"[AnimatorLod] {lod.name}: {report}", lod);
                        }
                    }
                    // 複数選択でも保存は 1 回にまとめる
                    AssetDatabase.SaveAssets();
                    serializedObject.Update();
                }
            }
        }

        /// <summary>
        /// 保存データは「Renderer ごとに LOD 別 Mesh の配列」(renderers[i].lodMeshes[level])のまま、
        /// 表示だけ LOD ごとのバンドに組み替える。
        /// LOD 0 は差し替えを行わず元 Mesh は References の SMR と重複するので表示しない(LOD 1 から)。
        /// バンド内の Element i は References の Skinned Mesh Renderers の i 番目の SMR 用(並び順も同じ)。
        /// </summary>
        private void DrawMeshLodLevels()
        {
            var bindings = _renderers;
            if (serializedObject.isEditingMultipleObjects && bindings.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Selected components have different Mesh LODs. Select one to edit.", MessageType.Info);
                return;
            }

            int levels = _lodRatios.arraySize + 1;
            for (int level = 1; level < levels; level++)
            {
                if (!BeginFixedList(MeshLodFoldoutKey(level), LodLabel(level), bindings.arraySize))
                {
                    continue;
                }
                for (int i = 0; i < bindings.arraySize; i++)
                {
                    var binding = bindings.GetArrayElementAtIndex(i);
                    var smr = binding.FindPropertyRelative("renderer").objectReferenceValue;
                    var label = ElementLabel(i);
                    label.tooltip = smr != null ? smr.name : "(missing)";
                    var meshes = binding.FindPropertyRelative("lodMeshes");
                    if (meshes.arraySize != levels)
                    {
                        meshes.arraySize = levels;
                    }
                    EditorGUILayout.PropertyField(meshes.GetArrayElementAtIndex(level), label);
                }
                EndFixedList();
            }
        }

        // ---- Skin Weights LOD ----

        private void DrawSkinWeightsSection()
        {
            using var _ = DrawEnabledToggle(_skinWeightsLodEnabled, SkinWeightsEnabledLabel);

            var qualities = _lodSkinQualities;
            int levels = _lodRatios.arraySize + 1;
            if (qualities.arraySize != levels)
            {
                qualities.arraySize = levels;
            }
            // LOD 0 は SMR 自身の quality をそのまま使う(配列の 0 番は位置合わせ用に残し、表示しない)
            for (int level = 1; level < levels; level++)
            {
                EditorGUILayout.PropertyField(qualities.GetArrayElementAtIndex(level), SkinLodLabel(level));
            }
        }

        /// <summary>
        /// セクションのバンド: helpBox 枠の中に
        /// Player Settings 風の FoldoutHeader を置き、開いていれば中身をインデントして描く。
        /// ヘッダーグループは見出しの直後で閉じる。配列プロパティ(ReorderableList)などが
        /// 自前の Foldout Header を持つため、中身までグループに含めると入れ子エラーになる。
        /// </summary>
        private static void DrawBand(string key, string title, bool defaultOpen, System.Action drawBody)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool open = SessionState.GetBool(key, defaultOpen);
                Rect rect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.foldoutHeader);
                rect.xMin += 13f; // 開閉矢印を helpBox 枠の内側に収める
                bool next = EditorGUI.BeginFoldoutHeaderGroup(rect, open, title);
                EditorGUILayout.EndFoldoutHeaderGroup();
                if (next != open)
                {
                    SessionState.SetBool(key, next);
                }

                if (next)
                {
                    // 中身は常に自分の枠から 1 段。入れ子のバンド(LOD 内の Animation LOD 等)でも外側の段数を足さない
                    int previousIndent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 1;
                    drawBody();
                    EditorGUI.indentLevel = previousIndent;
                }
            }
        }

        // ---- References ----

        /// <summary>References は表示のみ(編集不可)。</summary>
        private void DrawReferencesSection()
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(_animator, AnimatorLabel);
            }

            var bindings = _renderers;
            if (BeginFixedList(ReferencesListKey, RenderersLabel, bindings.arraySize))
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    for (int i = 0; i < bindings.arraySize; i++)
                    {
                        var label = ElementLabel(i);
                        label.tooltip = string.Empty;
                        EditorGUILayout.PropertyField(bindings.GetArrayElementAtIndex(i).FindPropertyRelative("renderer"), label);
                    }
                }
                EndFixedList();
            }

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button(RefreshReferencesLabel))
                {
                    foreach (var t in targets)
                    {
                        var lod = (AnimatorLod)t;
                        Undo.RecordObject(lod, "Refresh References");
                        lod.EditorFetchReferences();
                        EditorUtility.SetDirty(lod);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
                    }
                    serializedObject.Update();
                }
            }
        }

        /// <summary>
        /// 固定長配列の表示(+/- ボタン・並べ替えハンドルなし)の開始。折りたたみ見出しを描き、開いていれば true を返す。
        /// true のときは呼び出し側が Element i を描いてから <see cref="EndFixedList"/> を呼ぶ。
        /// References の Skinned Mesh Renderers と Mesh LOD の各 LOD で共通の見た目にするために使う。
        /// </summary>
        private static bool BeginFixedList(string foldoutKey, GUIContent title, int count)
        {
            bool open = SessionState.GetBool(foldoutKey, true);
            bool next = EditorGUILayout.Foldout(open, title, true);
            if (next != open)
            {
                SessionState.SetBool(foldoutKey, next);
            }
            if (!next)
            {
                return false;
            }

            EditorGUI.indentLevel++;
            if (count == 0)
            {
                EditorGUILayout.LabelField(EmptyListLabel, EditorStyles.miniLabel);
            }
            return true;
        }

        private static void EndFixedList()
        {
            EditorGUI.indentLevel--;
        }

        // ---- Bounds(LOD バンド内、Ratios の上) ----

        /// <summary>
        /// Bounds は LOD 判定の外接球として使う。
        /// </summary>
        private void DrawBoundsSection()
        {
            EditorGUILayout.PropertyField(_lodBounds, BoundsLabel);

            var source = _boundsSource;
            string status = source.hasMultipleDifferentValues
                ? StatusMixed
                : BoundsStatus((AnimatorLod.BoundsSource)source.intValue);
            EditorGUILayout.LabelField("Status", status);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(CalculateFromClipsLabel))
                {
                    ApplyBounds(AnimatorLod.BoundsSource.AllClips);
                }
                if (GUILayout.Button(CalculateFromDefaultPoseLabel))
                {
                    ApplyBounds(AnimatorLod.BoundsSource.DefaultPose);
                }
            }
        }

        private void ApplyBounds(AnimatorLod.BoundsSource mode)
        {
            foreach (var t in targets)
            {
                AnimatorLodBoundsCalculator.Apply((AnimatorLod)t, mode, true);
            }
            serializedObject.Update();
        }

        private static string BoundsStatus(AnimatorLod.BoundsSource source)
        {
            switch (source)
            {
                case AnimatorLod.BoundsSource.AllClips: return StatusAllClips;
                case AnimatorLod.BoundsSource.DefaultPose: return StatusDefaultPose;
                default: return StatusRenderers;
            }
        }

        // ---- Animation LOD ----

        private void DrawLodSection()
        {
            using var _ = DrawEnabledToggle(_lodEnabled, AnimationLodEnabledLabel);

            var intervals = _lodIntervals;
            var baseInterval = _baseInterval;
            int boundaryCount = _lodRatios.arraySize;
            if (intervals.arraySize != boundaryCount)
            {
                intervals.arraySize = boundaryCount;
            }

            EditorGUILayout.IntSlider(baseInterval, 0, AnimatorLod.MaxInterval, BaseIntervalLabel);
            EditorGUILayout.IntSlider(_invisibleInterval, 0, AnimatorLod.MaxInterval, InvisibleIntervalLabel);

            for (int level = 1; level <= boundaryCount; level++)
            {
                DrawIntervalRow(intervals, level, baseInterval.intValue);
            }
        }

        private static void DrawIntervalRow(SerializedProperty intervals, int level, int baseInterval)
        {
            int index = level - 1;
            if (index < 0 || index >= intervals.arraySize)
            {
                return;
            }
            var element = intervals.GetArrayElementAtIndex(index);
            int total = baseInterval + element.intValue;
            // 合計値を含むので固定ラベルにはできない。GUIContent は使い回し、文字列だけ差し替える
            s_intervalRowLabel.text = $"LOD {level} Interval (+Base = {total})";
            EditorGUILayout.IntSlider(element, 0, AnimatorLod.MaxInterval, s_intervalRowLabel);
        }

        private static void DrawTransitionRow(SerializedProperty ratios, SerializedProperty cull,
            float[] current, int i, int boundaryCount)
        {
            var element = ratios.GetArrayElementAtIndex(i);
            EditorGUI.BeginChangeCheck();
            float percent = EditorGUILayout.Slider(TransitionLabel(i + 1),
                RatioToPercent(element.floatValue), MinPercent, 100f);
            if (EditorGUI.EndChangeCheck())
            {
                element.floatValue = ClampBoundary(current, i, boundaryCount, PercentToRatio(percent), cull.floatValue);
            }
        }

        // ---- Ratios バー描画(StaticInstanceEditorCommon.DrawRatioBar と同じ配置規則) ----

        private static void DrawRatioBar(Rect rect, SerializedProperty ratios, SerializedProperty cull,
            float[] current, int boundaryCount)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));

            // 比率リニア: 左端 = 1.0、右端 = 0.0
            float PositionOf(float ratio) => rect.x + rect.width * (1f - Mathf.Clamp01(ratio));
            float RatioAt(float x) => 1f - Mathf.Clamp01((x - rect.x) / Mathf.Max(1f, rect.width));

            EnsureBarStyles();

            float cullValue = Mathf.Clamp01(cull.floatValue);
            float cullX = cullValue > 0f ? PositionOf(cullValue) : rect.xMax;

            // セグメント
            float previousX = rect.x;
            for (int level = 0; level <= boundaryCount; level++)
            {
                float nextX = level < boundaryCount
                    ? PositionOf(current[level])
                    : rect.xMax;
                var segment = new Rect(previousX, rect.y, Mathf.Max(0f, nextX - previousX), rect.height);
                var fillRect = new Rect(segment.x + 1f, segment.y + 1f, Mathf.Max(0f, segment.width - 2f), segment.height - 2f);
                EditorGUI.DrawRect(fillRect, SegmentColor(level, boundaryCount));

                float visibleRight = Mathf.Min(segment.xMax, cullX);
                var labelRect = new Rect(segment.x, segment.y, Mathf.Max(0f, visibleRight - segment.x), segment.height);
                if (labelRect.width > BarLabelMinWidth)
                {
                    GUI.Label(labelRect, SegmentName(level), s_segmentLabel);
                }
                previousX = nextX;
            }

            // Culled 帯
            if (cullValue > 0f)
            {
                var band = new Rect(cullX, rect.y, rect.xMax - cullX, rect.height);
                EditorGUI.DrawRect(band, new Color(0.10f, 0.10f, 0.10f, 0.85f));
                if (band.width > BarLabelMinWidth)
                {
                    GUI.Label(band, "Culled", s_culledLabel);
                }
            }

            // 境界ハンドル + 読み出し + ドラッグ
            for (int i = 0; i < boundaryCount; i++)
            {
                var element = ratios.GetArrayElementAtIndex(i);
                float x = PositionOf(element.floatValue);
                DrawHandle(rect, x, Color.white);

                float leftX = i > 0 ? PositionOf(current[i - 1]) : rect.x;
                float owningWidth = Mathf.Min(x, cullX) - leftX;
                // 読み出しの文字列は描画する Repaint のときだけ作る
                if (owningWidth > BarLabelMinWidth && Event.current.type == EventType.Repaint)
                {
                    GUI.Label(new Rect(x - 51f, rect.yMax - 14f, 48f, 13f),
                        RatioToPercent(element.floatValue).ToString("0.##") + "%", s_readoutLabel);
                }

                if (HandleDrag(rect, x, out float dragX))
                {
                    element.floatValue = ClampBoundary(current, i, boundaryCount, RatioAt(dragX), cullValue);
                }
            }

            // Culled ハンドル
            {
                DrawHandle(rect, cullX, cullValue > 0f ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.55f, 0.55f, 0.55f));
                if (cullValue > 0f && Event.current.type == EventType.Repaint)
                {
                    float leftX = boundaryCount > 0 ? PositionOf(current[boundaryCount - 1]) : rect.x;
                    if (cullX - leftX > BarLabelMinWidth)
                    {
                        GUI.Label(new Rect(cullX - 51f, rect.yMax - 14f, 48f, 13f),
                            RatioToPercent(cullValue).ToString("0.##") + "%", s_readoutLabel);
                    }
                }
                float grabX = cullValue > 0f ? cullX : cullX - 3f;
                if (HandleDrag(rect, grabX, out float dragX))
                {
                    cull.floatValue = ClampCull(current, boundaryCount, RatioAt(dragX));
                }
            }
        }

        private static void EnsureBarStyles()
        {
            if (s_segmentLabel != null)
            {
                return;
            }
            s_segmentLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            s_readoutLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };
            s_culledLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.72f, 0.72f, 0.72f) },
            };
        }

        private static void DrawHandle(Rect bar, float x, Color color)
        {
            EditorGUIUtility.AddCursorRect(new Rect(x - 3f, bar.y, 6f, bar.height), MouseCursor.ResizeHorizontal);
            EditorGUI.DrawRect(new Rect(x - 1f, bar.y, 2f, bar.height), color);
        }

        /// <summary>境界ハンドルのドラッグ処理。ドラッグ中のイベントでは true と現在のマウス X を返す。</summary>
        private static bool HandleDrag(Rect bar, float x, out float dragX)
        {
            dragX = 0f;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            var evt = Event.current;
            var handle = new Rect(x - 3f, bar.y, 6f, bar.height);
            switch (evt.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when handle.Contains(evt.mousePosition):
                    GUIUtility.hotControl = controlId;
                    evt.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    dragX = evt.mousePosition.x;
                    evt.Use();
                    return true;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    evt.Use();
                    break;
            }
            return false;
        }

        // ---- 番号付きラベルのキャッシュ ----

        private static GUIContent ElementLabel(int index) =>
            Cached(s_elementLabels, index, i => new GUIContent($"Element {i}"));

        private static GUIContent LodLabel(int level) =>
            Cached(s_lodLabels, level, i => new GUIContent($"LOD {i}"));

        private static GUIContent SkinLodLabel(int level) =>
            Cached(s_skinLodLabels, level, i => new GUIContent($"LOD {i}", "Skin weights for this LOD. Auto = renderer's own setting."));

        private static GUIContent TransitionLabel(int level) =>
            Cached(s_transitionLabels, level, i => new GUIContent($"LOD {i} Transition (% Screen Size)", $"Enter LOD {i} below this screen-size ratio."));

        private static string SegmentName(int level) =>
            Cached(s_segmentNames, level, i => $"LOD {i}");

        private static string MeshLodFoldoutKey(int level) =>
            Cached(s_meshLodFoldoutKeys, level, i => MeshLodKey + ".LOD" + i);

        private static readonly List<string> s_meshLodFoldoutKeys = new List<string>();

        private static T Cached<T>(List<T> cache, int index, System.Func<int, T> create)
        {
            while (cache.Count <= index)
            {
                cache.Add(create(cache.Count));
            }
            return cache[index];
        }

        // ---- 共通 ----

        // float の比率を ×100 すると 9.999999 のような誤差が出るため、double で計算して丸める。
        // 表示は % 小数 4 桁、保存は比率 小数 6 桁。
        private static float RatioToPercent(float ratio) =>
            (float)System.Math.Round(ratio * 100.0, 4);

        private static float PercentToRatio(float percent) =>
            (float)System.Math.Round(percent * 0.01, 6);

        private static Color SegmentColor(int level, int boundaryCount)
        {
            return Color.Lerp(
                new Color(0.24f, 0.52f, 0.30f),
                new Color(0.30f, 0.36f, 0.52f),
                boundaryCount > 0 ? level / (float)boundaryCount : 0f);
        }

        private static float[] Snapshot(SerializedProperty ratios)
        {
            var values = new float[ratios.arraySize];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ratios.GetArrayElementAtIndex(i).floatValue;
            }
            return values;
        }

        private static bool LaddersAgree(SerializedProperty ratios)
        {
            if (ratios.hasMultipleDifferentValues)
            {
                return false;
            }
            for (int i = 0; i < ratios.arraySize; i++)
            {
                if (ratios.GetArrayElementAtIndex(i).hasMultipleDifferentValues)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>境界 i を隣(上は i-1、下は i+1 または Culled)の間にクランプする。降順。</summary>
        private static float ClampBoundary(float[] current, int i, int boundaryCount, float value, float cullRatio)
        {
            float upper = i > 0 ? current[i - 1] : 1f;
            float lower = i + 1 < boundaryCount
                ? current[i + 1]
                : Mathf.Max(cullRatio, AnimatorLod.MinRatio);
            return Mathf.Clamp(value, lower, upper);
        }

        private static float ClampCull(float[] current, int boundaryCount, float value)
        {
            float last = boundaryCount > 0 ? current[boundaryCount - 1] : 1f;
            return Mathf.Clamp(value, 0f, last);
        }
    }
}
