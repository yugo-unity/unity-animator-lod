# OptimizedAnimators

[English](README.md) | 日本語

大量の Animator を表示するときの CPU/GPU 負荷を下げるための検証用プロジェクトです。中心となるのは `AnimatorLod` コンポーネントと、それを一括で処理する `AnimatorLodSystem` です。

## 構成

| 種類 | ファイル | 役割 |
|---|---|---|
| ランタイム | `Assets/Scripts/AnimatorLod.cs` | Animator を持つ GameObject に 1 つ付ける個体単位の設定と状態。`Animator` が必須コンポーネント |
| ランタイム | `Assets/Scripts/AnimatorLodSystem.cs` | 登録された全個体の LOD 判定と Animator の切替を 1 か所で行う静的クラス。PlayerLoop の Update 段の末尾(ユーザースクリプトの Update、コルーチン、async の継続、DirectorUpdate の後)に挿入されるので、これらで行ったステートの変更や `RequestImmediateUpdate()` を同じフレームの Animator の評価より前に反映できる。状態は Play の開始時と終了時に作り直すため、ドメインリロードを無効にした Enter Play Mode Options でも動作する |
| Editor | `Assets/Scripts/Editor/AnimatorLodEditor.cs` | Inspector と Scene View の LOD 表示 |
| Editor | `Assets/Scripts/Editor/AnimatorLodBoundsCalculator.cs` | Static Bounds 用の AABB を全クリップから実測する |
| Editor | `Assets/Scripts/Editor/AnimatorLodMeshReducer.cs` | Mesh LOD 用のリダクションメッシュを生成する |
| ベンチマーク | `Assets/Benchmark/` | 検証シーン(AnimatorLodTest / AnimatorStressTest)用のスポナー・HUD・操作パネル。`Test/` は実機計測用のスクリプト(Development ビルドのプレイヤーを起動引数 `-alodStaticBoundsBench` 付きで起動したときだけ動作) |

## Inspector

### References

表示のみで編集できません。どちらもコンポーネントをアタッチした時点で自動取得されます。

| 名前 | 説明 |
|---|---|
| Animator | 同じ GameObject の Animator です。`Animator` は必須コンポーネントです。 |
| Skinned Mesh Renderers | 子階層(非アクティブを含む)の SkinnedMeshRenderer すべてです。 |
| Refresh References | 両方を取得し直します。階層を変更したあとに使います。 |

実行時に未設定のままだった場合は、従来どおり Awake で取得します。その際は実行時に探索が走るため、警告ログを出します。参照が失われた Renderer(Refresh References の後に階層から削除した場合など)は、Awake で警告ログを出して対象から外します。

Renderer は単独で破棄しても構いません(装備の付け替えなど)。次にその Renderer を扱う時点で警告ログを出し、対象から外します。Animator は AnimatorLod と同時にだけ破棄される前提です。AnimatorLod が有効な間に Animator を単独で破棄することは想定していません(ランタイムのコードは Animator を null チェックしません)。

各 Renderer とあわせて、その Renderer 自身の設定(メッシュ、rootBone、localBounds、Skin Weights)と Static Bounds 用の固定 Bounds も Editor で準備しておくため、Awake では探索や変換を行いません。これらは Inspector で AnimatorLod を編集したとき、Calculate Bounding の実行時、Refresh References の実行時に自動で取り直されます。**AnimatorLod を触らずに Renderer のメッシュ、rootBone、Skin Weights を後から変更した場合は、Refresh References を押してください。** 押さないと古い値が残り、ランタイムで各機能を無効にしたときにその古い値へ戻されます。

### LOD

LOD の境界は Animation LOD・Mesh LOD・Skin Weights で共通です。

| 名前 | 説明 |
|---|---|
| Bounds | このコンポーネントの Transform ローカル空間での AABB です。常に LOD 判定の基準球(外接球)として使います。コンポーネントをアタッチした時点で、Calculate Bounding と同じ方法で計算されます。 |
| Calculate Bounding | Animator Controller の全クリップ・全ポーズをサンプリングし、すべてのポーズを包含する Bounds を実測します。Animator に Controller が設定されていない場合は、エラーにせず Renderer の現在の Bounds(現在のポーズ)を使います。ランタイムでは計算しません。 |
| Static Bounds | SkinnedMeshRenderer の rootBone を外し、Bounds を固定の localBounds として与えます。この機能のオンオフだけを切り替え、Bounds はどちらの場合も LOD 判定に使います。既定は無効です(下記の注意を参照)。 |
| Ratios | Bounds の外接球が画面高さに占める割合(Screen Size 比)で LOD を選びます。左が 100%(近)、右が 0%(遠)で、境界をドラッグして編集します。 |
| LOD n Transition (% Screen Size) | Screen Size 比がこの値を下回ると LOD n に入ります。隣の境界を越えて設定することはできません(降順)。 |
| Culled (% Screen Size) | この値を下回ると LOD カリングとして扱い、Invisible Interval を適用します。0 で無効です。Mesh LOD と Skin Weights は最後の LOD の設定を使います。 |
| - Level / + Level | LOD の段数を増減します。 |

Static Bounds の注意:

- 固定の Bounds は全クリップ・全ポーズを包むため、rootBone に追従する既定の Bounds(インポート時のもの)より大きくなります。そのぶん画面外の個体もカリングされにくくなり、描画とスキニングの対象が増えます。AnimatorLodTest(既定設定、200 体)では、描画される SkinnedMesh が 160 から 170 に、ドローコールと三角形数が約 6〜8% 増えました(Editor で計測)。
- SkinnedMeshRenderer の Update When Offscreen が無効(既定)の場合は、Static Bounds を使わなくてもボーンから Bounds を毎フレーム計算し直す処理は発生しません(Profiler で `Mesh.CalcBoneBounds` が記録されないことを確認)。省ける処理はわずかで、上の描画負荷の増加のほうが大きくなります。
- 効果が見込めるのは、Update When Offscreen を有効にしてボーンから Bounds を毎フレーム計算している場合です(未計測)。

#### Animation LOD

| 名前 | 説明 |
|---|---|
| Enabled | 毎フレームの LOD 判定を行います。**Mesh LOD と Skin Weights もこれが有効なときだけ動作します。** |
| LOD Camera | Screen Size 比の計算に使うカメラです。未設定なら Camera.main を使います。 |
| Base Interval | 全 LOD に共通で加算される Animator の更新間隔(フレーム)です。LOD 0 はこの値だけで動きます。0/1 は毎フレームです。 |
| Invisible Interval | 配下の Renderer がどのカメラにも描かれていないとき、または Culled のときに、LOD と Base を無視して適用する更新間隔です。可視判定は Animator のカリングと同じ基準(Renderer.isVisible)です。ただし Animator は配下の全 Renderer を見るのに対し、こちらは References の Renderer だけを見ます。 |
| LOD n Interval | LOD n で Base Interval に加算する更新間隔です。有効な間隔は Base + この値です。 |

更新間隔 N のとき、Animator は N フレームに 1 回だけ有効化されます。有効化したフレームでは `speed` を「`Speed` × 前回の評価からの経過フレーム数」にし、間引いた時間を補償します。経過フレーム数は定常状態では N になり、更新間隔が変わった直後(LOD・可視状態の変化や、Base / Invisible Interval のランタイム変更)や `RequestImmediateUpdate()` の後もずれません。同じ間隔の個体は位相バケットに分散され、特定のフレームに集中しません。コンポーネントが有効になった直後の最初の判定では、生成直後の個体がプレハブのポーズのまま描かれないよう、間隔に関わらず 1 回評価します。

Animator の Update Mode が Fixed(Animate Physics)の場合は非対応です。Animator が FixedUpdate で評価されるため、フレーム数による間引きと `speed` の補償が合いません。登録時に警告ログを出します(Play ごとに 1 回)。Normal または Unscaled Time を使ってください。

#### Mesh LOD

| 名前 | 説明 |
|---|---|
| Enabled | LOD に応じて SkinnedMeshRenderer.sharedMesh をリダクション済みのメッシュに差し替え、Skinning の頂点数を減らします。 |
| LOD n / Element i | LOD n で References の i 番目の SkinnedMeshRenderer に使うメッシュです。空欄なら元のメッシュを使います。LOD 0 は常に元のメッシュです。 |
| Generate Reduced Meshes | Unity 内蔵の Mesh LOD 生成器(`MeshLodUtility.GenerateMeshLods`、Editor 専用)で簡略化し、各レベルが使う頂点だけを取り出した独立メッシュを `Assets/Generated/MeshLod/` に書き出して、上の欄に割り当てます。 |

注意:

- 差し替え用のメッシュは元のメッシュと同じスケルトン(bindposes の順序と本数)である必要があります。生成ツールはボーン重みと bindposes を元のメッシュから引き継ぎます。
- Optimize Game Objects を有効にしたリグでは、SkinnedMeshRenderer がメッシュのボーン名ハッシュで Animator のスケルトンに結合します。生成ツールはこのハッシュも元のメッシュから写します。
- Unity 標準の Mesh LOD(Import 設定の Generate Mesh LODs)は頂点バッファを共有し、インデックス範囲だけを切り替えます。描画の三角形は減りますが、Skinning の頂点数は減りません。そのため独立したメッシュとして書き出しています。
- 生成物の見た目の品質は考慮していません(検証用)。
- 再生成すると既存のアセットに上書きします(GUID と参照は保たれます)。
- 効果の出る場所は Skinning 方式で変わります。CPU Skinning では CPU 時間が、GPU Skinning では GPU 時間が減ります。

#### Skin Weights

| 名前 | 説明 |
|---|---|
| Enabled | LOD に応じて SkinnedMeshRenderer.quality(頂点あたりのボーン数)を切り替えます。 |
| LOD n | LOD n で使う Skin Weights です。Auto は SkinnedMeshRenderer 自身の設定を使います。LOD 0 は常に SkinnedMeshRenderer 自身の設定です。 |

注意:

- `QualitySettings.skinWeights` が上限です。それより多いボーン数を指定しても効きません。
- エンジンはボーン重みのキャッシュをメッシュごと・ボーン数ごとに初回使用時に生成します。有効にすると、コンポーネントが有効になった時点、またはランタイムで Skin Weights を有効にした時点で、使用するメッシュとボーン数の組み合わせについて事前にキャッシュを作ります(同じメッシュは 1 回だけ)。
- キャッシュ済みかどうかの記録は Mesh への参照として保持し、Play が終わるまで(ビルドではアプリケーションが終わるまで)消しません。ランタイムで生成したメッシュを Mesh LOD に使い、それを破棄して作り直すような使い方では、破棄済みのメッシュの記録が作った数だけ残ります(1 件は小さく、通常の使い方では問題になりません)。

## Scene View

Hierarchy で AnimatorLod を選択すると、その頭上に現在の LOD が表示されます。

- Play 中: AnimatorLodSystem の判定結果(LOD、Screen Size 比、Interval、位相バケット、可視状態、Mesh LOD と Skin Weights の現在値)。
- 編集中: LOD Camera(未設定なら Camera.main、それも無ければ Scene カメラ)で、ランタイムと同じ式を使ったプレビュー。

## ランタイム API

| API | 内容 |
|---|---|
| `SetStaticBounds(bool)` | Static Bounds の有効/無効 |
| `SetLodEnabled(bool)` | Animation LOD の有効/無効 |
| `SetMeshLodEnabled(bool)` | Mesh LOD の有効/無効。無効化時は元のメッシュに戻す |
| `SetSkinWeightsLodEnabled(bool)` | Skin Weights の有効/無効。有効化時にキャッシュを作り、無効化時は元の設定に戻す |
| `BaseInterval` / `InvisibleInterval` | 更新間隔の取得と設定 |
| `Speed` | Animator の再生速度。`Animator.speed` の代わりに使う(下記) |
| `RequestImmediateUpdate()` | 外部から Animator のステートやパラメータを変更した直後に呼ぶ。間引き中でも次の判定で 1 回評価して即時反映する。コンポーネントが無効な間と Animation LOD が無効な間の要求は捨てる(有効化直後と LOD 無効中は要求が無くても評価されるため) |
| `AnimatorLodSystem.Instances` | 登録中の全個体。Play ごとに別のリストになるため、Play をまたいで保持しない |
| `AnimatorLodSystem.InvisibleCount` / `EnabledThisFrame` | 直近の判定で不可視だった個体数と、Animator を有効化した個体数 |

`SetStaticBounds`、`SetMeshLodEnabled`、`SetSkinWeightsLodEnabled` が Renderer に即時反映されるのは、コンポーネントが有効な間だけです。無効な間は設定値だけを保持し、再び有効になった後に反映されます。

登録中の個体の `Animator.enabled` と `Animator.speed` は `AnimatorLodSystem` が管理します。外部から変更しないでください。

- **速度**: `Animator.speed` ではなく `AnimatorLod.Speed` を設定してください(スローモーションや、0 での一時停止など)。次の評価フレームで「`Speed` × 経過フレーム数」として反映されます。コンポーネントが有効になった時点の `Animator.speed` を `Speed` の初期値として取り込み、無効になったときは `Animator.speed` を `Speed` に戻します。無効な間は `Animator.speed` を直接変えても構いません。
- **Animator の停止**: 先に AnimatorLod を無効にしてから Animator を無効にしてください。AnimatorLod は無効になったときに Animator を有効な状態に戻します。

Play 中に Inspector で変更した値(LOD Camera、Static Bounds、Bounds、各 LOD のメッシュや Skin Weights など)は、次の判定で反映されます(Editor のみ)。

コンポーネントを新しく追加したときの既定値は、Static Bounds、Mesh LOD、Skin Weights がいずれも無効です。

## 最適化余地(未実装)

### LOD 設定の共有アセット化(ScriptableObject プロファイル)

プレハブの全個体で共通の LOD 設定(Ratios、LOD n Transition / Interval、Culled、Base / Invisible Interval、LOD ごとの Skin Weights)を `AnimatorLod` から ScriptableObject のプロファイルに移し、各 `AnimatorLod` はプロファイルを参照する方式です。

- **狙い**: 現在は個体ごとに、Instantiate 時にこれらの設定を 3 本の配列(`lodRatios`、`lodIntervals`、`lodSkinQualities`)を含めてデシリアライズしています。プロファイルを共有すると、これらの配列は個体ごとではなくプロファイルごとに 1 つで済み、生成時の GC アロケートとメモリが減ります。参考として、現状 `Armature_Lod` の Instantiate は `AnimatorLod` を持たない同じオブジェクトより 1 体あたり 17 件多く GC アロケートします(Editor で実測)。プロファイル化で減るのは配列 1 本につき 1 件、計 3 件程度と見込んでいます(推定、未計測)。
- **副次効果**: プロファイルを編集すると、それを使うキャラクター全体に一度に反映されます。
- **見送った理由**: ワークフローがやや複雑になるためです。
  - キャラクターの種類ごとにアセットを作成し、割り当てる手間が増えます。
  - Inspector での編集が共有アセットの変更になり、それを使う全個体に影響します。
  - 個体ごとに調整するには、プロファイルを分けるか上書きの仕組みが必要です。
  - `BaseInterval` / `InvisibleInterval` は現在個体単位のランタイム設定なので、コンポーネント側に残すか意味を変える必要があります。
- **対象外**: Mesh LOD のメッシュと Renderer ごとのデータは、個体自身の Renderer 参照に結び付くため、コンポーネント側に残します。
