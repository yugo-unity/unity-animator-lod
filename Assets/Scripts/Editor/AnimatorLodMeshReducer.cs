using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace AnimatorStressTest.Editor
{
    /// <summary>
    /// AnimatorLod 配下の SkinnedMeshRenderer の sharedMesh から、LOD level ごとに頂点数を削った独立 Mesh を生成する。
    ///
    /// 簡略化は Unity 内蔵の Mesh LOD 生成器(MeshLodUtility.GenerateMeshLods、Editor 専用)に任せる。
    /// ただし Mesh LOD は「頂点バッファ共有 + index 範囲」なので、そのままでは Skinning 頂点数が減らない。
    /// そこで各 level の index 範囲が参照する頂点だけを抽出し、別 Mesh として書き出す(ボーン重み・bindposes は元を引き継ぐ)。
    /// 生成物は Assets/Generated/MeshLod/ に保存し、AnimatorLod の Mesh LOD テーブルへ自動で割り当てる。
    /// 検証用途のため見た目の品質は考慮しない。
    /// </summary>
    public static class AnimatorLodMeshReducer
    {
        private const string OutputFolder = "Assets/Generated/MeshLod";

        /// <summary>
        /// 生成して AnimatorLod の Mesh LOD テーブルに割り当てる。
        /// saveAssets = false なら保存は呼び出し側に任せる(複数対象をまとめて 1 回で保存するため)。
        /// </summary>
        public static bool Generate(AnimatorLod lod, out string report, bool saveAssets = true)
        {
            Undo.RecordObject(lod, "Generate Reduced Meshes");
            var renderers = lod.EditorSkinnedMeshRenderers;
            if (renderers.Length == 0)
            {
                // 生成結果は Renderer ごとのデータに書き込むため、References を先に準備する
                lod.EditorFetchReferences();
                renderers = lod.EditorSkinnedMeshRenderers;
            }
            if (renderers.Length == 0)
            {
                report = "SkinnedMeshRenderer が見つかりません";
                return false;
            }

            int levels = lod.LodLevelCount; // LOD 0..N
            if (levels <= 1)
            {
                report = "LOD level が 1 つしかないため生成対象がありません";
                return false;
            }

            EnsureFolder(OutputFolder);

            string ownerName = SanitizeFileName(lod.gameObject.name);
            var results = new Mesh[renderers.Length][];
            int generatedCount = 0;
            var sb = new System.Text.StringBuilder();

            for (int r = 0; r < renderers.Length; r++)
            {
                var smr = renderers[r];
                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }

                var source = smr.sharedMesh;
                var sourceAttributes = new SourceAttributes(source);
                var lodMeshes = new Mesh[levels];
                sb.Append(smr.name).Append(": LOD0 verts=").Append(source.vertexCount);

                // 元 Mesh のコピーに Mesh LOD を生成(元アセットは触らない)
                var work = Object.Instantiate(source);
                work.name = source.name + "_work";
                try
                {
                    MeshLodUtility.GenerateMeshLods(work, -1);
                    int generated = work.lodCount;

                    for (int level = 1; level < levels; level++)
                    {
                        int genLevel = Mathf.Min(level, generated - 1);
                        if (genLevel <= 0)
                        {
                            sb.Append(" LOD").Append(level).Append("=none");
                            continue;
                        }

                        string path = $"{OutputFolder}/{ownerName}_{SanitizeFileName(smr.name)}_LOD{level}.asset";

                        // 既存アセットがあればそれに直接書き込む(GUID と参照を保つ)。
                        // CopySerialized でスクリプト生成 Mesh を写すと頂点データが落ちるため使わない。
                        var target = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                        bool isNew = target == null;
                        if (isNew)
                        {
                            target = new Mesh();
                        }
                        ExtractLevel(work, sourceAttributes, genLevel, target);
                        // アセットのメインオブジェクト名はファイル名と一致させる(不一致だと Import 時に警告)
                        target.name = Path.GetFileNameWithoutExtension(path);
                        CopyBoneNameHashes(source, target);
                        if (isNew)
                        {
                            AssetDatabase.CreateAsset(target, path);
                        }
                        else
                        {
                            EditorUtility.SetDirty(target);
                        }

                        lodMeshes[level] = target;
                        sb.Append(" LOD").Append(level).Append(" verts=").Append(target.vertexCount)
                          .Append(" tris=").Append(TriangleCount(target));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(work);
                }
                sb.Append('\n');

                results[r] = lodMeshes;
                generatedCount++;
            }

            // 対象外の Renderer(Mesh 無し)は差し替え無し(元 Mesh)にする
            for (int r = 0; r < renderers.Length; r++)
            {
                lod.EditorSetLodMeshes(r, results[r]);
            }
            EditorUtility.SetDirty(lod);
            PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
            if (saveAssets)
            {
                AssetDatabase.SaveAssets();
            }

            report = sb.ToString().TrimEnd();
            return generatedCount > 0;
        }

        /// <summary>三角形数。triangles プロパティは index 配列を丸ごと確保するため、サブメッシュの index 数から求める。</summary>
        private static long TriangleCount(Mesh mesh)
        {
            long indices = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                indices += mesh.GetIndexCount(s);
            }
            return indices / 3;
        }

        /// <summary>元 Mesh の頂点属性。LOD level ごとに読み直さないよう、元 Mesh ごとに 1 度だけ取得する。</summary>
        private sealed class SourceAttributes
        {
            public readonly int VertexCount;
            public readonly Vector3[] Vertices;
            public readonly Vector3[] Normals;
            public readonly Vector4[] Tangents;
            public readonly Vector2[] Uv;
            public readonly byte[] BonesPerVertex;
            public readonly BoneWeight1[] BoneWeights;
            /// <summary>頂点 v のボーン重みが BoneWeights の何番目から始まるか。</summary>
            public readonly int[] BoneWeightStart;
            public readonly Matrix4x4[] Bindposes;

            public SourceAttributes(Mesh source)
            {
                VertexCount = source.vertexCount;
                Vertices = source.vertices;
                Normals = source.normals;
                Tangents = source.tangents;
                Uv = source.uv;
                Bindposes = source.bindposes;
                // GetBonesPerVertex / GetAllBoneWeights は Mesh が所有する配列を返すので、コピーして保持する
                BonesPerVertex = source.GetBonesPerVertex().ToArray();
                BoneWeights = source.GetAllBoneWeights().ToArray();
                BoneWeightStart = new int[BonesPerVertex.Length];
                int offset = 0;
                for (int v = 0; v < BonesPerVertex.Length; v++)
                {
                    BoneWeightStart[v] = offset;
                    offset += BonesPerVertex[v];
                }
            }
        }

        /// <summary>work の Mesh LOD level が参照する頂点だけを抜き出し、target に書き込む。頂点属性とボーン重みは元 Mesh(src)から取る。</summary>
        private static void ExtractLevel(Mesh work, SourceAttributes src, int level, Mesh mesh)
        {
            int srcVertexCount = src.VertexCount;
            var remap = new int[srcVertexCount];
            for (int i = 0; i < remap.Length; i++)
            {
                remap[i] = -1;
            }

            int subMeshCount = work.subMeshCount;
            var newSubIndices = new List<int>[subMeshCount];
            var usedOrder = new List<int>(srcVertexCount / 2);

            // Mesh LOD の各 level はサブメッシュの index 範囲(indexStart 起点)の中に LOD0 の後ろへ追記されている。
            // GetIndices(sub) は LOD0 分しか返さないので、生の index バッファを直接読む。
            using (var dataArray = Mesh.AcquireReadOnlyMeshData(work))
            {
                var data = dataArray[0];
                bool is16 = data.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16;
                var idx16 = is16 ? data.GetIndexData<ushort>() : default;
                var idx32 = is16 ? default : data.GetIndexData<uint>();

                for (int s = 0; s < subMeshCount; s++)
                {
                    var sub = work.GetSubMesh(s);
                    var range = work.GetLod(s, level);
                    var list = new List<int>((int)range.indexCount);
                    int start = sub.indexStart + (int)range.indexStart;
                    int end = start + (int)range.indexCount;
                    for (int k = start; k < end; k++)
                    {
                        int v = (is16 ? idx16[k] : (int)idx32[k]) + sub.baseVertex;
                        if (remap[v] < 0)
                        {
                            remap[v] = usedOrder.Count;
                            usedOrder.Add(v);
                        }
                        list.Add(remap[v]);
                    }
                    newSubIndices[s] = list;
                }
            }

            int newCount = usedOrder.Count;

            var vertices = new Vector3[newCount];
            var normals = src.Normals.Length == srcVertexCount ? new Vector3[newCount] : null;
            var tangents = src.Tangents.Length == srcVertexCount ? new Vector4[newCount] : null;
            var uv = src.Uv.Length == srcVertexCount ? new Vector2[newCount] : null;
            // ボーン重みは頂点ごとの本数が可変(旧 API の boneWeights は 4 本までに切り詰めるため使わない)
            bool hasWeights = src.BonesPerVertex.Length == srcVertexCount;
            var bonesPerVertex = hasWeights ? new byte[newCount] : null;
            var weights = hasWeights ? new List<BoneWeight1>(newCount * 4) : null;

            for (int i = 0; i < newCount; i++)
            {
                int v = usedOrder[i];
                vertices[i] = src.Vertices[v];
                if (normals != null) normals[i] = src.Normals[v];
                if (tangents != null) tangents[i] = src.Tangents[v];
                if (uv != null) uv[i] = src.Uv[v];
                if (hasWeights)
                {
                    byte count = src.BonesPerVertex[v];
                    bonesPerVertex[i] = count;
                    int first = src.BoneWeightStart[v];
                    for (int w = 0; w < count; w++)
                    {
                        weights.Add(src.BoneWeights[first + w]);
                    }
                }
            }

            mesh.Clear();
            mesh.indexFormat = newCount > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            if (normals != null) mesh.SetNormals(normals);
            if (tangents != null) mesh.SetTangents(tangents);
            if (uv != null) mesh.SetUVs(0, uv);
            if (hasWeights)
            {
                using (var nativeBones = new NativeArray<byte>(bonesPerVertex, Allocator.Temp))
                using (var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                {
                    mesh.SetBoneWeights(nativeBones, nativeWeights);
                }
            }
            mesh.bindposes = src.Bindposes;

            mesh.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
            {
                mesh.SetTriangles(newSubIndices[s], s, false);
            }
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
        }

        /// <summary>
        /// Optimize Game Objects(bones 配列なし)の SMR は、Mesh の m_BoneNameHashes / m_RootBoneNameHash で
        /// Animator のスケルトンに結合する。本数が bindposes と一致しないと "Bones do not match bindpose." のエラーで結合されない。
        /// スクリプトで作った Mesh は空なので元 Mesh から写す。
        /// ボーン index は変えていないので、元のハッシュ配列をそのまま使える。
        /// </summary>
        private static void CopyBoneNameHashes(Mesh source, Mesh target)
        {
            var src = new SerializedObject(source);
            var dst = new SerializedObject(target);
            var srcHashes = src.FindProperty("m_BoneNameHashes");
            var dstHashes = dst.FindProperty("m_BoneNameHashes");
            if (srcHashes != null && dstHashes != null)
            {
                dstHashes.arraySize = srcHashes.arraySize;
                for (int i = 0; i < srcHashes.arraySize; i++)
                {
                    dstHashes.GetArrayElementAtIndex(i).longValue = srcHashes.GetArrayElementAtIndex(i).longValue;
                }
            }
            var srcRoot = src.FindProperty("m_RootBoneNameHash");
            var dstRoot = dst.FindProperty("m_RootBoneNameHash");
            if (srcRoot != null && dstRoot != null)
            {
                dstRoot.longValue = srcRoot.longValue;
            }
            dst.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
