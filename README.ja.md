# OptimizedAnimators

[English](README.md) | 日本語

大量の Animator を表示するときの CPU/GPU 負荷を下げるための検証用プロジェクトです。中心となるのは `AnimatorLod` コンポーネントと、それを一括で処理する `AnimatorLodSystem` です。

## 構成

| 種類 | ファイル | 役割 |
|---|---|---|
| ランタイム | `Assets/Scripts/AnimatorLod.cs` | Animator を持つ GameObject に 1 つ付ける個体単位の設定と状態。`Animator` が必須コンポーネント |
| ランタイム | `Assets/Scripts/AnimatorLodSystem.cs` | 登録された全個体の LOD 判定と Animator の切替を 1 か所で行う静的クラス。PlayerLoop の Update 段の末尾(ユーザースクリプトの Update、コルーチン、async の継続、DirectorUpdate の後)に挿入されるので、これらで行ったステートの変更や `RequestImmediateUpdate()` を同じフレームの Animator の評価より前に反映できる。状態は Play の開始時と終了時に作り直すため、ドメインリロードを無効にした Enter Play Mode Options でも動作する |
| Editor | `Assets/Scripts/Editor/AnimatorLodEditor.cs` | Inspector と Scene View の LOD 表示 |
| Editor | `Assets/Scripts/Editor/AnimatorLodBoundsCalculator.cs` | LOD 判定に使う Bounds を全クリップまたはデフォルトポーズから実測する |
| Editor | `Assets/Scripts/Editor/AnimatorLodMeshReducer.cs` | Mesh LOD 用のリダクションメッシュを生成する |
| ベンチマーク | `Assets/Benchmark/` | 検証シーン(AnimatorLodTest)用のスポナー・HUD・操作パネル |

## Inspector

### References

表示のみで編集できません。どちらもコンポーネントをアタッチした時点で自動取得されます。

| 名前 | 説明 |
|---|---|
| Animator | 同じ GameObject の Animator です。`Animator` は必須コンポーネントです。 |
| Skinned Mesh Renderers | 子階層(非アクティブを含む)の SkinnedMeshRenderer すべてです。 |
| Refresh References | 両方を取得し直します。階層を変更したあとに使います。 |

参照とあわせて、各 Renderer 自身の設定(メッシュ、Skin Weights)も Editor で準備しておくため、Awake では探索や変換を行いません。これらは Inspector で AnimatorLod を編集したときと、Refresh References の実行時に自動で取り直されます。**AnimatorLod を触らずに Renderer のメッシュや Skin Weights を後から変更した場合は、Refresh References を押してください。** 押さないと古い値が残り、ランタイムで各機能を無効にしたときにその古い値へ戻されます。

Editor で準備されていないコンポーネント(実行時に `AddComponent` で追加したものなど)は非対応です。Awake でエラーログを出し、自身を無効にします。Editor で追加してプレハブに含めてください。

参照が失われた Renderer(Refresh References の後に階層から削除した場合など)は、Awake で警告ログを出して対象から外します。Renderer は単独で破棄しても構いません(装備の付け替えなど)。次にその Renderer を扱う時点で警告ログを出し、対象から外します。Animator は AnimatorLod と同時にだけ破棄される前提です。AnimatorLod が有効な間に Animator を単独で破棄することは想定していません(ランタイムのコードは Animator を null チェックしません)。

### LOD

LOD の境界と LOD Camera は Animation LOD・Mesh LOD・Skin Weights で共通です。3 つのうちどれかが有効なら LOD を判定します。判定は 3 つで共通の 1 回で、機能ごとに重複して行うことはありません。

LOD の判定は毎フレームではありません。

- Animation LOD が有効な間は、Animator を評価するフレームでだけ LOD を判定します。可視状態が変わったとき、`RequestImmediateUpdate()` の後、設定を変えたとき(`Set*Enabled` や Inspector での変更)、コンポーネントが有効になった直後の最初の判定では、すぐに判定します。可視判定そのものは毎フレーム行うので、画面に入った個体はすぐに更新が再開されます。
- Animation LOD が無効な間(Mesh LOD / Skin Weights のみ)は、LOD Evaluation Interval フレームに 1 回、個体ごとにずらして判定します。

このため LOD の切り替わり(Mesh LOD / Skin Weights の切り替えを含む)は、最大でその個体の更新間隔の分、Animation LOD が無効な間は最大で LOD Evaluation Interval の分遅れます。

| 名前 | 説明 |
|---|---|
| Bounds | このコンポーネントの Transform ローカル空間での AABB です。LOD 判定の基準球(外接球)として使います。コンポーネントをアタッチした時点で、Calculate from All Clips と同じ方法で計算されます。選択中は Scene View に AABB(黄)と外接球(水色)を表示します。 |
| Status | Bounds をどこから求めたかです。Calculated from all clips / Calculated from default pose / Taken from renderer bounds(Controller が無いときのアタッチ時など)。 |
| Calculate from All Clips | Animator Controller の全クリップ・全ポーズをサンプリングし、すべてのポーズを包含する Bounds を実測します。空中のクリップなどで足元より下や左右非対称に広がることがあります。Animator に Controller が設定されていない場合は、エラーにせず Renderer の現在の Bounds(現在のポーズ)を使います。ランタイムでは計算しません。 |
| Calculate from Default Pose | アニメーションを適用しないデフォルトポーズ(プレハブ / シーン上の個体に保存されたボーンの姿勢)で Bounds を実測します。Controller の有無や Optimize Game Objects に関係なく使えます。ポーズによってはメッシュが Bounds からはみ出します。ランタイムでは計算しません。 |
| LOD Camera | Screen Size 比の計算に使うカメラです。未設定なら Camera.main を使います。どちらも無い場合は Screen Size 比を 100%(LOD 0)として扱います。判定に使うカメラは 1 台だけです(画面分割などで複数のカメラから見る場合は考慮しません)。 |
| LOD Evaluation Interval | Animation LOD が無効な間の判定間隔(フレーム、1〜8)です(上記参照)。1 は毎フレームです。Animation LOD が有効な間と、Mesh LOD・Skin Weights がともに無効な間はグレーアウトします。 |
| Ratios | Bounds の外接球が画面高さに占める割合(Screen Size 比)で LOD を選びます。左が 100%(近)、右が 0%(遠)で、境界をドラッグして編集します。 |
| LOD n Transition (% Screen Size) | Screen Size 比がこの値を下回ると LOD n に入ります。隣の境界を越えて設定することはできません(降順)。 |
| Culled (% Screen Size) | この値を下回ると LOD カリングとして扱い、Invisible Interval を適用します。0 で無効です。Mesh LOD と Skin Weights は最後の LOD の設定を使います。 |
| - Level / + Level | LOD の段数を増減します。 |

Screen Size 比は近似です。外接球の半径は、Bounds の外接球の半径に各軸のワールドスケールの最大を掛けて求めます(各軸のスケールは `localToWorldMatrix` の列ベクトルの長さで、せん断が無ければ `lossyScale` の絶対値と同じ)。透視投影と正射影のどちらのカメラにも対応します。透視投影では「この半径 ÷ (カメラまでの距離 × tan(FOV / 2))」、正射影では「この半径 ÷ `orthographicSize`」で、正射影の値は距離に依存しません。

#### Animation LOD

| 名前 | 説明 |
|---|---|
| Enabled | LOD に応じて Animator の更新を間引きます。無効にしても Mesh LOD と Skin Weights は動作します(Animator は毎フレーム評価されます)。 |
| Base Interval | 全 LOD に共通で加算される Animator の更新間隔(フレーム、0〜4)です。LOD 0 はこの値だけで動きます。0/1 は毎フレームです。 |
| Invisible Interval | 配下の Renderer がどのカメラにも描かれていないとき、または Culled のときに、LOD と Base を無視して適用する更新間隔(フレーム、0〜4)です。可視判定は Animator のカリングと同じ基準(Renderer.isVisible)です。ただし Animator は配下の全 Renderer を見るのに対し、こちらは References の Renderer だけを見ます。 |
| LOD n Interval | LOD n で Base Interval に加算する更新間隔(フレーム、0〜4)です。有効な間隔は Base + この値です(最大 8)。 |

更新間隔 N のとき、Animator は N フレームに 1 回だけ有効化されます。有効化したフレームでは `speed` を「`Speed` × 前回の評価からの経過時間 ÷ このフレームの deltaTime」にし、間引いた時間を補償します。経過時間は Animator の Update Mode に合わせて通常の時刻か unscaled の時刻で測るので、フレームレートが変動しても、間引いた各フレームの deltaTime の合計どおりに進みます(フレームレートが一定なら経過フレーム数と同じです)。更新間隔が変わった直後(LOD・可視状態の変化や、Base / Invisible Interval のランタイム変更)や `RequestImmediateUpdate()` の後もずれません。同じ間隔の個体は位相バケットに分散され、特定のフレームに集中しません。コンポーネントが有効になった直後の最初の判定では、生成直後の個体がプレハブのポーズのまま描かれないよう、間隔に関わらず 1 回評価します。

間引いたフレームでは Animator 自体が動かないため、次の点に注意してください(Editor で確認)。

- スクリプトから変更したパラメータやトリガーは、次に評価するフレームで反映されます。すぐ反映したいときは `RequestImmediateUpdate()` を呼んでください。
- アニメーションイベント、`OnAnimatorMove`、`OnAnimatorIK`、ルートモーション、ボーンに付けたオブジェクトの動きは、評価するフレームでだけ処理され、間引いた時間の分がまとめて進みます。アニメーションイベントは失われませんが、最大で(更新間隔 − 1)フレーム遅れて発火します。

Animator の Update Mode が Fixed(Animate Physics)の場合は非対応です。Animator が FixedUpdate で評価されるため、フレーム単位の間引きと `speed` の補償が合いません。登録時に警告ログを出します(Play ごとに 1 回)。Normal または Unscaled Time を使ってください。

#### Mesh LOD

| 名前 | 説明 |
|---|---|
| Enabled | LOD に応じて SkinnedMeshRenderer.sharedMesh をリダクション済みのメッシュに差し替え、Skinning の頂点数を減らします。 |
| LOD n / Element i | LOD n で References の i 番目の SkinnedMeshRenderer に使うメッシュです。空欄なら元のメッシュを使います。LOD 0 は常に元のメッシュです。 |
| Generate Reduced Meshes | Unity 内蔵の Mesh LOD 生成器(`MeshLodUtility.GenerateMeshLods`、Editor 専用)で簡略化し、各レベルが使う頂点だけを取り出した独立メッシュを `Assets/Generated/MeshLod/` に書き出して、上の欄に割り当てます。 |

注意:

- 差し替え用のメッシュは元のメッシュと同じスケルトン(bindposes の順序と本数)である必要があります。生成ツールはボーン重みと bindposes を元のメッシュから引き継ぎます。
- Optimize Game Objects を有効にしたリグでは、SkinnedMeshRenderer がメッシュのボーン名ハッシュで Animator のスケルトンに結合します。生成ツールはこのハッシュも元のメッシュから写します。
- 生成したメッシュは Read/Write を無効にして保存します(メインメモリに複製を残しません)。そのため、LOD メッシュが適用されている間に実行時のスクリプトから `SkinnedMeshRenderer.sharedMesh` の頂点などを読むと、エラーログが出て空の配列が返ります(例外は出ません。Editor の Play 中とプレイヤーで確認)。また、その間の `sharedMesh` は LOD メッシュを返します。
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

#### ボーン重みバッファの事前作成(Mesh LOD / Skin Weights 共通)

GPU Skinning は、メッシュとボーン数の組ごとに、初めて描画するときにボーン重みの GPU バッファを作ります。そのフレームに作成の負荷が乗ります。

- AnimatorLod は、現在の Mesh LOD / Skin Weights の設定で LOD 1 以降が使う組についてだけ、このバッファを事前に作ります。LOD 0 の組は通常の描画で作られるため除きます。同じ組は 1 回だけです。
- 事前に作るのは、コンポーネントが有効になったとき、ランタイムで Mesh LOD または Skin Weights を有効・無効にしたとき、Play 中に Inspector で変更したとき(Editor のみ)です。
- 個体の生成より前(ロード中など)に作っておきたい場合は `Prewarm()` を呼んでください。プレハブのアセットに対しても呼べます(インスタンスを作らず、アセットも書き換えません)。
- 実行中に `QualitySettings.skinWeights` を変えた場合、新しく必要になったボーン数のバッファは事前には作られず、初めて描画するときに作られます(描画は正しく行われます)。変更後に `Prewarm()` を呼び直すと事前に作れます。
- 作成済みの記録はメッシュを弱参照で持つため、`Resources.UnloadUnusedAssets` や AssetBundle の解放によるメッシュのアンロードを妨げません。アンロード後に読み直したメッシュは、あらためて作成します。アンロード・破棄されたメッシュの記録は、次に新しいメッシュを記録するときに削除します。

## Scene View

Hierarchy で AnimatorLod を選択すると、その頭上に現在の LOD が表示されます。

- Play 中: AnimatorLodSystem の判定結果(LOD、Screen Size 比、Interval、位相バケット、可視状態、Mesh LOD と Skin Weights の現在値)。
- 編集中: LOD Camera(未設定なら Camera.main、それも無ければ Scene カメラ)で、ランタイムと同じ式を使ったプレビュー。

## ランタイム API

| API | 内容 |
|---|---|
| `SetLodEnabled(bool)` | Animation LOD の有効/無効 |
| `SetMeshLodEnabled(bool)` | Mesh LOD の有効/無効。切り替えたときに必要なボーン重みバッファを事前に作り、無効化時は元のメッシュに戻す |
| `SetSkinWeightsLodEnabled(bool)` | Skin Weights の有効/無効。切り替えたときに必要なボーン重みバッファを事前に作り、無効化時は元の設定に戻す |
| `Prewarm()` | Mesh LOD / Skin Weights が使うボーン重みバッファを今すぐ作る。プレハブのアセットに対しても呼べる |
| `BaseInterval` / `InvisibleInterval` | 更新間隔の取得と設定。0〜4 の範囲外の値は丸める |
| `LodEvaluationInterval` | LOD Evaluation Interval の取得と設定。1〜8 の範囲外の値は丸める |
| `Speed` | Animator の再生速度。`Animator.speed` の代わりに使う(下記) |
| `RequestImmediateUpdate()` | 外部から Animator のステートやパラメータを変更した直後に呼ぶ。間引き中でも次の判定で 1 回評価して即時反映する。コンポーネントが無効な間と Animation LOD が無効な間の要求は捨てる(有効化直後と Animation LOD 無効中は要求が無くても評価されるため) |
| `AnimatorLodSystem.Instances` | 登録中の全個体。Play ごとに別のリストになるため、Play をまたいで保持しない |
| `AnimatorLodSystem.InvisibleCount` / `EnabledThisFrame` | 直近の判定で不可視だった個体数と、Animator を有効化した個体数 |

読み取り専用の状態(個体ごと):

| API | 内容 |
|---|---|
| `LodEnabled` / `MeshLodEnabled` / `SkinWeightsLodEnabled` | Animation LOD / Mesh LOD / Skin Weights が有効か |
| `LodCamera` / `LodBounds` / `LodBoundsSource` | LOD Camera、Bounds、Bounds をどこから求めたか(Inspector の Status) |
| `LodLevelCount` | LOD の段数(境界の数 + 1) |
| `GetEffectiveInterval(level)` | LOD `level` の有効な更新間隔(Base Interval + LOD n Interval。Invisible Interval は考慮しない) |
| `CurrentLod` | 直近の判定の LOD。最初の判定の前と、コンポーネントまたは 3 機能がすべて無効な間は -1。Culled の間は `LodLevelCount` |
| `CurrentScreenRatio` / `IsCulled` | 直近の判定の Screen Size 比と Culled かどうか |
| `IsInvisible` / `CurrentInterval` | 直近の判定の可視状態と、適用した更新間隔。Animation LOD が無効な間は常に `false` / 0 |
| `CurrentBucket` | 割り当て中の位相バケット(毎フレーム評価の間は 0) |

`SetMeshLodEnabled`、`SetSkinWeightsLodEnabled` が Renderer に即時反映されるのは、コンポーネントが有効な間だけです。無効な間は設定値だけを保持し、再び有効になった後に反映されます。

登録中の個体の `Animator.enabled` と `Animator.speed` は `AnimatorLodSystem` が管理します。外部から変更しないでください。

- **速度**: `Animator.speed` ではなく `AnimatorLod.Speed` を設定してください(スローモーションや、0 での一時停止など)。次の評価フレームで、間引いた時間を補償した値として反映されます。コンポーネントが有効になった時点の `Animator.speed` を `Speed` の初期値として取り込み、無効になったときは `Animator.speed` を `Speed` に戻します。無効な間は `Animator.speed` を直接変えても構いません。
- **Animator の停止**: 先に AnimatorLod を無効にしてから Animator を無効にしてください。AnimatorLod は無効になったときに Animator を有効な状態に戻します。

Play 中に Inspector で変更した値(LOD Camera、Bounds、各 LOD のメッシュや Skin Weights など)は、次の判定で反映されます(Editor のみ)。

コンポーネントを新しく追加したときの既定値は、Mesh LOD と Skin Weights がどちらも無効です。

## 制約

上記の各節の注意をまとめたものです。

- `Animator.speed` と `Animator.enabled` は直接変更できません(`Speed` を使う、停止は AnimatorLod を先に無効にする)。
- Awake で `Animator.keepAnimatorStateOnDisable` を `true` に設定します。この設定は AnimatorLod を外した後も残ります。
- Mesh LOD と Skin Weights は `SkinnedMeshRenderer.sharedMesh` と `quality` を書き換えます。同じ Renderer のメッシュを実行中に差し替える(衣装替えなど)と競合します。機能を無効にしたときは、Editor で記録した元の値に戻します。
- 間引いたフレームでは Animator が動きません。パラメータの反映、アニメーションイベント、`OnAnimatorMove`、IK、ルートモーション、ボーンに付けたオブジェクトは、評価するフレームでまとめて進みます(Animation LOD の節を参照)。
- LOD の判定は、Bounds の外接球と各軸のワールドスケールの最大を使った近似です。判定に使うカメラは 1 台だけです。
- 差し替え用のメッシュは、元のメッシュと同じスケルトン(bindposes の順序と本数)である必要があります。
- 生成した LOD メッシュは Read/Write が無効です。適用中に実行時のスクリプトから頂点などを読むと、空の配列が返ります。
- Editor で準備されていないコンポーネント(実行時の `AddComponent` など)は非対応です。
- Animator の Update Mode が Fixed(Animate Physics)の場合は非対応です。
- `AnimatorLodSystem` は PlayerLoop の Update 段の末尾に 1 回だけ挿入されます。他のシステムが後から `PlayerLoop.SetPlayerLoop` で既定のツリーを丸ごと設定し直すと、この処理が外れ、次の Play(ビルドでは次の起動)まで動きません。

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
