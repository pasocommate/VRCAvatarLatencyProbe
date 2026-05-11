#if UNITY_EDITOR
using PasocomMate.VRCAvatarLatencyProbe;
using System.Reflection;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 落下閾値通過方式レイテンシ計測ブースのシーン階層を構築するエディタヘルパー。
///
/// メニューバーから実行:
///   LatencyProbe > Setup Booth
///
/// レイアウト (上から見た図, -Z が入口):
///
///              +Z (奥)
///        ┌──────────────┐
///        │   BackWall   │
///        │              │
///   -X   │  (落下軸)    │  +X   ← DisplayPanel (右壁外面)
///        │              │
///        │  FrontUpper  │
///        └──┐  入口  ┌──┘
///           └────────┘
///           ExitPosition
///              -Z (手前)
/// </summary>
public static class LatencyProbeSceneSetup
{
    // ================================================================
    // アセット GUID 定数 (.meta ファイルから取得)
    // ================================================================
    private const string GUID_MAT_TRIGGER_INDICATOR = "3fb536e46964d8f4981c5b86b4082088";
    private const string GUID_MAT_ENCLOSURE_BODY    = "2e4ba52b42e17fc498a9f9bf5829bcc3";
    private const string GUID_MAT_DOOR_FRAME        = "d0da1c7640e87d548bd8ba6b3a5e00e7";
    private const string GUID_MAT_BUTTON_FACE       = "1e9f9c32e03946e41a37935e5eff2439";
    private const string GUID_MAT_BEACON_GREEN      = "dad2e652b07f83446b9ce1e6bcfec94d";
    private const string GUID_MAT_BEACON_RED        = "d6a92bc33a2b4474f99aafb6a4e5361d";
    private const string GUID_BOOTH_BUTTON_ASSET    = "3782d6a2c80db544ebf96a0e0e41c166";

    // Canvas の論理解像度 (pixels)。transform.localScale で世界空間サイズに変換する。
    // 1px = 1mm → scale = 0.001
    private const float CANVAS_WIDTH_PX  = 1200f;
    private const float CANVAS_HEIGHT_PX = 900f;
    private const float CANVAS_SCALE     = 0.001f; // → 1.2m × 0.9m

    // 落下パラメータ
    private const float FALL_START_HEIGHT   = 10f; // 落下開始 Y
    private const float ENCLOSURE_HEIGHT    = 12f; // 筐体高さ
    private const int   NUM_FALL_INDICATORS = 5;   // 落下進捗表示数

    // エンクロージャー寸法
    private const float BOOTH_HALF_W = 1.1f; // X 半幅 (内寸 2.0m + 壁厚 0.1m×2)
    private const float BOOTH_HALF_D = 1.1f; // Z 半奥行
    private const float WALL_THICK   = 0.1f;
    private const float ENTRANCE_H   = 2.5f; // 入口の高さ

    // エレベーター ドア枠
    private const float DOOR_HALF_W   = 0.8f;  // ドア開口の半幅
    private const float DOOR_FRAME_W  = 0.12f;  // 枠柱の幅
    private const float DOOR_FRAME_D  = 0.05f;  // 枠の奥行

    // ボタン
    private const float BUTTON_HEIGHT = 1.0f;   // ボタン中心の高さ
    private const float BUTTON_SIZE   = 0.12f;  // ボタンのサイズ

    // ================================================================
    // メニュー項目
    // ================================================================

    [MenuItem("_PasocomMate-Dev/LatencyProbe/Setup Booth")]
    private static void SetupBooth()
    {
        CreateObjects();
        WireReferences();
        Debug.Log("[LatencyProbe] セットアップ完了 — オブジェクト生成 + 参照ワイヤリング");
    }

    // ----------------------------------------------------------------
    // Step 1 実装
    // ----------------------------------------------------------------

    private static void CreateObjects()
    {
        // 既存オブジェクトがあれば全て削除（重複防止）
        GameObject existing;
        while ((existing = GameObject.Find("LatencyProbeBooth")) != null
               && existing.transform.parent == null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        // ---- ルート ----
        GameObject root = new GameObject("LatencyProbeBooth");
        Undo.RegisterCreatedObjectUndo(root, "Setup LatencyProbe Step1");

        // ---- TeleportLatencyProbe ----
        GameObject probeGo = CreateChild(root, "TeleportLatencyProbe");
        UdonSharpComponentExtensions.AddUdonSharpComponent<TeleportLatencyProbe>(probeGo);

        // ---- FallStart（落下開始位置、入口 (-Z) を向く）----
        GameObject fallStartGo = CreateChild(root, "FallStart");
        fallStartGo.transform.localPosition = new Vector3(0f, FALL_START_HEIGHT, 0f);
        fallStartGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f);

        // ---- DisplayPanel (World Space Canvas) — 右壁外面、目線の高さ ----
        GameObject displayGo = CreateChild(root, "DisplayPanel");
        displayGo.transform.localPosition = new Vector3(
            BOOTH_HALF_W + 0.06f,  // 右壁外面の直近
            1.3f,                  // 目線の高さ (1.5m アバターの目線 ≈ 1.4m)
            -0.3f);                // 入口寄り
        displayGo.transform.localEulerAngles = new Vector3(0f, 270f, 0f); // +X 方向を向く

        Canvas canvas = displayGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRt = displayGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(CANVAS_WIDTH_PX, CANVAS_HEIGHT_PX);
        displayGo.transform.localScale = Vector3.one * CANVAS_SCALE;

        var scaler = displayGo.AddComponent<CanvasScaler>();
        scaler.scaleFactor = 1f;
        scaler.dynamicPixelsPerUnit = 1f;

        UdonSharpComponentExtensions.AddUdonSharpComponent<ProbeDisplay>(displayGo);

        // ---- Background Image ----
        GameObject bgGo = CreateChild(displayGo, "Background");
        var bgImage = bgGo.AddComponent<Image>();
        bgImage.color = new Color(0f, 0f, 0f, 0.75f);
        RectTransform bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        // ---- StatusText（上部ステータスエリア）----
        GameObject statusGo = CreateChild(displayGo, "StatusText");
        var statusText = statusGo.AddComponent<TextMeshProUGUI>();
        statusText.fontSize = 26;
        statusText.color = Color.white;
        statusText.alignment = TextAlignmentOptions.TopLeft;
        statusText.text = "待機中";
        RectTransform statusRt = statusGo.GetComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(0f, 0.82f);
        statusRt.anchorMax = new Vector2(1f, 1f);
        statusRt.offsetMin = new Vector2(30f, 0f);
        statusRt.offsetMax = new Vector2(-30f, -15f);

        // ---- LogText（下部結果エリア — 計測結果を永続表示）----
        GameObject logGo = CreateChild(displayGo, "LogText");
        var logText = logGo.AddComponent<TextMeshProUGUI>();
        logText.fontSize = 27;
        logText.color = Color.white;
        logText.alignment = TextAlignmentOptions.TopLeft;
        logText.text = "";
        RectTransform logRt = logGo.GetComponent<RectTransform>();
        logRt.anchorMin = new Vector2(0f, 0f);
        logRt.anchorMax = new Vector2(1f, 0.78f);
        logRt.offsetMin = new Vector2(30f, 20f);
        logRt.offsetMax = new Vector2(-30f, 0f);

        // ---- トライアル進捗インジケータ (DisplayPanel Canvas 内) ----
        float indicatorSize = 35f;
        float indicatorSpacing = 55f;
        float totalW = NUM_FALL_INDICATORS * indicatorSpacing;
        float startX = -totalW / 2f + indicatorSpacing / 2f;

        for (int i = 0; i < NUM_FALL_INDICATORS; i++)
        {
            GameObject indGo = CreateChild(displayGo, $"TrialIndicator_{i}");
            var indImg = indGo.AddComponent<Image>();
            indImg.color = new Color(0.3f, 0.3f, 0.3f, 1f);
            RectTransform indRt = indGo.GetComponent<RectTransform>();
            indRt.anchorMin = new Vector2(0.5f, 0.80f);
            indRt.anchorMax = new Vector2(0.5f, 0.80f);
            indRt.sizeDelta = new Vector2(indicatorSize, indicatorSize);
            indRt.anchoredPosition = new Vector2(startX + i * indicatorSpacing, 0f);
        }

        // ---- HUDPanel（アクター頭部追従 HUD）----
        GameObject hudGo = CreateChild(root, "HUDPanel");
        hudGo.SetActive(false);
        hudGo.transform.localPosition = Vector3.zero;

        Canvas hudCanvas = hudGo.AddComponent<Canvas>();
        hudCanvas.renderMode = RenderMode.WorldSpace;
        RectTransform hudRt = hudGo.GetComponent<RectTransform>();
        hudRt.sizeDelta = new Vector2(400f, 70f);
        hudGo.transform.localScale = Vector3.one * 0.001f;
        hudGo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;

        // HUD 背景
        GameObject hudBgGo = CreateChild(hudGo, "HUDBackground");
        var hudBg = hudBgGo.AddComponent<Image>();
        hudBg.color = new Color(0f, 0f, 0f, 0.65f);
        var hudBgRt = hudBgGo.GetComponent<RectTransform>();
        hudBgRt.anchorMin = Vector2.zero;
        hudBgRt.anchorMax = Vector2.one;
        hudBgRt.offsetMin = Vector2.zero;
        hudBgRt.offsetMax = Vector2.zero;

        // HUD テキスト
        GameObject hudTextGo = CreateChild(hudGo, "HUDText");
        var hudText = hudTextGo.AddComponent<TextMeshProUGUI>();
        hudText.fontSize = 27;
        hudText.color = Color.white;
        hudText.alignment = TextAlignmentOptions.Center;
        hudText.text = "計測中  0 / 5";
        var hudTextRt = hudTextGo.GetComponent<RectTransform>();
        hudTextRt.anchorMin = Vector2.zero;
        hudTextRt.anchorMax = Vector2.one;
        hudTextRt.offsetMin = new Vector2(10f, 5f);
        hudTextRt.offsetMax = new Vector2(-10f, -5f);

        // ---- TriggerFloorIndicator ----
        GameObject floorIndGo = CreateChild(root, "TriggerFloorIndicator");
        floorIndGo.AddComponent<MeshFilter>().sharedMesh =
            Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        var floorIndRenderer = floorIndGo.AddComponent<MeshRenderer>();

        string matDir = GetAssetDirectoryByGuid(
            GUID_MAT_TRIGGER_INDICATOR,
            "Packages/tokyo.chigiri.pasocommate.vrcavatarlatencyprobe/Materials");
        EnsureDirectory(matDir);

        Material floorMat = GetOrCreateStandardFadeMat(GUID_MAT_TRIGGER_INDICATOR,
            matDir + "/TriggerIndicator.mat",
            new Color(0.2f, 0.8f, 1f, 0.4f), metallic: 0f, smoothness: 0.3f);
        floorIndRenderer.sharedMaterial = floorMat;
        floorIndGo.transform.localPosition = new Vector3(0f, 0.001f, 0f);
        floorIndGo.transform.localEulerAngles = new Vector3(90f, 0f, 0f);
        floorIndGo.transform.localScale = new Vector3(2.0f, 2.0f, 1f);

        // ================================================================
        // エレベーター風エンクロージャー (-Z に入口)
        // ================================================================

        // マテリアル
        Material wallMat = GetOrCreateStandardMat(GUID_MAT_ENCLOSURE_BODY,
            matDir + "/EnclosureBody.mat",
            new Color(0.55f, 0.55f, 0.58f, 1f), metallic: 0.1f, smoothness: 0.3f);
        Material doorFrameMat = GetOrCreateStandardMat(GUID_MAT_DOOR_FRAME,
            matDir + "/DoorFrame.mat",
            new Color(0.45f, 0.45f, 0.48f, 1f), metallic: 0.6f, smoothness: 0.7f);
        Material buttonMat = GetOrCreateStandardEmissiveMat(GUID_MAT_BUTTON_FACE,
            matDir + "/ButtonFace.mat",
            new Color(0.05f, 0.15f, 0.12f, 1f),
            new Color(0.2f, 1f, 0.6f) * 1.5f, smoothness: 0.8f);
        Material beaconGreenMat = GetOrCreateStandardEmissiveMat(GUID_MAT_BEACON_GREEN,
            matDir + "/BeaconGreen.mat",
            new Color(0.05f, 0.3f, 0.05f, 1f),
            new Color(0.1f, 1f, 0.2f) * 2f, smoothness: 0.8f);
        Material beaconRedMat = GetOrCreateStandardEmissiveMat(GUID_MAT_BEACON_RED,
            matDir + "/BeaconRed.mat",
            new Color(0.3f, 0.05f, 0.05f, 1f),
            new Color(1f, 0.15f, 0.1f) * 2f, smoothness: 0.8f);

        AssetDatabase.SaveAssets();

        float fullW = BOOTH_HALF_W * 2f;   // 2.2
        float fullD = BOOTH_HALF_D * 2f;   // 2.2
        float halfH = ENCLOSURE_HEIGHT / 2f;

        GameObject enclosure = CreateChild(root, "Enclosure");

        // -- 壁 --
        CreateCubePanel(enclosure, "BackWall", wallMat,
            new Vector3(0f, halfH, BOOTH_HALF_D),
            new Vector3(fullW, ENCLOSURE_HEIGHT, WALL_THICK));
        CreateCubePanel(enclosure, "LeftWall", wallMat,
            new Vector3(-BOOTH_HALF_W, halfH, 0f),
            new Vector3(WALL_THICK, ENCLOSURE_HEIGHT, fullD));
        CreateCubePanel(enclosure, "RightWall", wallMat,
            new Vector3(BOOTH_HALF_W, halfH, 0f),
            new Vector3(WALL_THICK, ENCLOSURE_HEIGHT, fullD));
        CreateCubePanel(enclosure, "Roof", wallMat,
            new Vector3(0f, ENCLOSURE_HEIGHT, 0f),
            new Vector3(fullW, WALL_THICK, fullD));

        // -- 正面: ドア開口の左右壁 + 上部壁 --
        float frontUpperH = ENCLOSURE_HEIGHT - ENTRANCE_H;
        CreateCubePanel(enclosure, "FrontUpper", wallMat,
            new Vector3(0f, ENTRANCE_H + frontUpperH / 2f, -BOOTH_HALF_D),
            new Vector3(fullW, frontUpperH, WALL_THICK));

        float sidePanelW = BOOTH_HALF_W - DOOR_HALF_W - DOOR_FRAME_W;
        if (sidePanelW > 0.01f)
        {
            float sidePanelX = DOOR_HALF_W + DOOR_FRAME_W + sidePanelW / 2f;
            CreateCubePanel(enclosure, "FrontLeftPanel", wallMat,
                new Vector3(-sidePanelX, ENTRANCE_H / 2f, -BOOTH_HALF_D),
                new Vector3(sidePanelW, ENTRANCE_H, WALL_THICK));
            CreateCubePanel(enclosure, "FrontRightPanel", wallMat,
                new Vector3(sidePanelX, ENTRANCE_H / 2f, -BOOTH_HALF_D),
                new Vector3(sidePanelW, ENTRANCE_H, WALL_THICK));
        }

        // -- ドア枠 (メタリック) --
        CreateCubePanel(enclosure, "DoorFrameLeft", doorFrameMat,
            new Vector3(-(DOOR_HALF_W + DOOR_FRAME_W / 2f), ENTRANCE_H / 2f, -BOOTH_HALF_D),
            new Vector3(DOOR_FRAME_W, ENTRANCE_H, DOOR_FRAME_D));
        CreateCubePanel(enclosure, "DoorFrameRight", doorFrameMat,
            new Vector3(DOOR_HALF_W + DOOR_FRAME_W / 2f, ENTRANCE_H / 2f, -BOOTH_HALF_D),
            new Vector3(DOOR_FRAME_W, ENTRANCE_H, DOOR_FRAME_D));
        CreateCubePanel(enclosure, "DoorFrameHeader", doorFrameMat,
            new Vector3(0f, ENTRANCE_H, -BOOTH_HALF_D),
            new Vector3(DOOR_HALF_W * 2f + DOOR_FRAME_W * 2f, DOOR_FRAME_W, DOOR_FRAME_D));

        // -- ステータスビーコン (ドア枠上部の外側) --
        GameObject beaconGo = CreateChild(enclosure, "StatusBeacon");
        beaconGo.transform.localPosition = new Vector3(0f, ENTRANCE_H + 0.2f, -(BOOTH_HALF_D + 0.06f));
        beaconGo.transform.localScale = new Vector3(0.5f, 0.06f, 0.03f);
        beaconGo.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        beaconGo.AddComponent<MeshRenderer>().sharedMaterial = beaconGreenMat;

        // -- 計測開始ボタン (ドア横の正面パネル内面、エレベーター式) --
        float btnX = -(DOOR_HALF_W + DOOR_FRAME_W + sidePanelW / 2f); // FrontLeftPanel の中心 X（内側から見て右手）
        float btnZ = -(BOOTH_HALF_D - WALL_THICK / 2f);            // 正面壁の内面

        // ボタン台座 (正面壁内面に貼り付け、ブース内側を向く) — 5×5 / 15×1 を縦に並べる
        float btn5x5Y = BUTTON_HEIGHT + 0.15f;
        float btn15x1Y = BUTTON_HEIGHT - 0.15f;

        CreateCubePanel(enclosure, "ButtonBackplate5x5", doorFrameMat,
            new Vector3(btnX, btn5x5Y, btnZ + 0.01f),
            new Vector3(sidePanelW - 0.04f, 0.20f, 0.02f));
        CreateCubePanel(enclosure, "ButtonBackplate15x1", doorFrameMat,
            new Vector3(btnX, btn15x1Y, btnZ + 0.01f),
            new Vector3(sidePanelW - 0.04f, 0.20f, 0.02f));

        // ボタン本体 5×5 (Interact 用の非トリガーコライダー付き)
        GameObject button5x5Go = CreateChild(root, "BoothButton5x5");
        button5x5Go.transform.localPosition = new Vector3(btnX, btn5x5Y, btnZ + 0.03f);
        button5x5Go.transform.localScale = new Vector3(BUTTON_SIZE, BUTTON_SIZE, 0.04f);
        button5x5Go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        button5x5Go.AddComponent<MeshRenderer>().sharedMaterial = buttonMat;
        button5x5Go.AddComponent<BoxCollider>().isTrigger = false;

        // ボタン本体 15×1
        GameObject button15x1Go = CreateChild(root, "BoothButton15x1");
        button15x1Go.transform.localPosition = new Vector3(btnX, btn15x1Y, btnZ + 0.03f);
        button15x1Go.transform.localScale = new Vector3(BUTTON_SIZE, BUTTON_SIZE, 0.04f);
        button15x1Go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        button15x1Go.AddComponent<MeshRenderer>().sharedMaterial = buttonMat;
        button15x1Go.AddComponent<BoxCollider>().isTrigger = false;

        // BoothButton の UdonSharp コンポーネントは Wire References で追加する

        // 配置
        root.transform.position = Vector3.zero;

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = root;

        Debug.Log("[LatencyProbe] オブジェクト生成完了");
    }

    // ----------------------------------------------------------------
    // Step 2 実装
    // ----------------------------------------------------------------

    private static void WireReferences()
    {
        var probeProxy = FindProxy<TeleportLatencyProbe>("LatencyProbeBooth/TeleportLatencyProbe");
        var displayProxy  = FindProxy<ProbeDisplay>("LatencyProbeBooth/DisplayPanel");

        if (probeProxy == null || displayProxy == null)
        {
            Debug.LogError("[LatencyProbe] オブジェクトが見つかりません。");
            return;
        }

        // Transform 参照を取得
        var fallStart = GameObject.Find("LatencyProbeBooth/FallStart")?.transform;
        if (fallStart == null)
        {
            Debug.LogError("[LatencyProbe] FallStart が見つかりません");
            return;
        }

        // UI 参照を取得
        var statusText = GameObject.Find("LatencyProbeBooth/DisplayPanel/StatusText")
            ?.GetComponent<TextMeshProUGUI>();
        var logText = GameObject.Find("LatencyProbeBooth/DisplayPanel/LogText")
            ?.GetComponent<TextMeshProUGUI>();
        var boothTf     = GameObject.Find("LatencyProbeBooth")?.transform;
        var hudRootTf   = boothTf?.Find("HUDPanel");
        var hudRoot     = hudRootTf?.gameObject;
        var hudText     = hudRootTf?.Find("HUDText")?.GetComponent<TextMeshProUGUI>();

        // ---- ProbeDisplay ----
        SetField(displayProxy, "_probe",   probeProxy);
        SetField(displayProxy, "_statusText", statusText);
        SetField(displayProxy, "_logText",    logText);
        SetField(displayProxy, "_hudRoot",    hudRoot);
        SetField(displayProxy, "_hudText",    hudText);

        // StatusBeacon 参照
        var beaconTf = boothTf?.Find("Enclosure/StatusBeacon");
        if (beaconTf != null)
        {
            SetField(displayProxy, "_statusBeacon", beaconTf.GetComponent<MeshRenderer>());
            SetField(displayProxy, "_beaconGreenMat",
                LoadAssetByGuid<Material>(GUID_MAT_BEACON_GREEN));
            SetField(displayProxy, "_beaconRedMat",
                LoadAssetByGuid<Material>(GUID_MAT_BEACON_RED));
        }

        // 床インジケータ参照
        var floorTf = boothTf?.Find("TriggerFloorIndicator");
        if (floorTf != null)
            SetField(displayProxy, "_floorIndicator", floorTf.GetComponent<MeshRenderer>());

        // トライアル進捗インジケータ参照
        var displayTf = boothTf?.Find("DisplayPanel");
        if (displayTf != null)
        {
            Image[] trialImages = new Image[NUM_FALL_INDICATORS];
            for (int i = 0; i < NUM_FALL_INDICATORS; i++)
            {
                var indTf = displayTf.Find($"TrialIndicator_{i}");
                if (indTf != null)
                    trialImages[i] = indTf.GetComponent<Image>();
            }
            SetField(displayProxy, "_trialIndicators", trialImages);
        }

        UdonSharpEditorUtility.CopyProxyToUdon(displayProxy);

        // ---- TeleportLatencyProbe ----
        SetField(probeProxy, "_display",   displayProxy);
        SetField(probeProxy, "_fallStart", fallStart);
        UdonSharpEditorUtility.CopyProxyToUdon(probeProxy);

        // ---- BoothButton (5×5 / 15×1) ----
        var programAsset = LoadAssetByGuid<Object>(GUID_BOOTH_BUTTON_ASSET);
        if (programAsset == null)
        {
            Debug.LogError(
                "[LatencyProbe] BoothButton の U# プログラムアセットが見つかりません (GUID: " +
                GUID_BOOTH_BUTTON_ASSET + ")。\n" +
                "Tools > UdonSharp > Refresh All UdonSharp Programs を実行後、再度 Setup Booth を実行してください。");
        }
        else
        {
            WireBoothButton(probeProxy, "LatencyProbeBooth/BoothButton5x5",
                false, "計測開始 (5×5)");
            WireBoothButton(probeProxy, "LatencyProbeBooth/BoothButton15x1",
                true, "計測開始 (15×1)");
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[LatencyProbe] 参照ワイヤリング完了");
    }

    // ----------------------------------------------------------------
    // ユーティリティ
    // ----------------------------------------------------------------

    private static void WireBoothButton(
        TeleportLatencyProbe probeProxy, string path,
        bool singleFallMode, string interactionText)
    {
        var go = GameObject.Find(path);
        if (go == null)
        {
            Debug.LogError($"[LatencyProbe] {path} が見つかりません。");
            return;
        }

        var proxy = FindProxy<BoothButton>(path);
        if (proxy == null)
            proxy = UdonSharpComponentExtensions.AddUdonSharpComponent<BoothButton>(go);

        SetField(proxy, "_probe", probeProxy);
        SetField(proxy, "_singleFallMode", singleFallMode);
        UdonSharpEditorUtility.CopyProxyToUdon(proxy);

        var udon = go.GetComponent<VRC.Udon.UdonBehaviour>();
        if (udon != null)
        {
            var so = new SerializedObject(udon);
            var prop = so.FindProperty("interactionText");
            if (prop != null)
            {
                prop.stringValue = interactionText;
                so.ApplyModifiedProperties();
            }
        }
    }

    private static GameObject CreateChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        Undo.RegisterCreatedObjectUndo(go, "Setup LatencyProbe");
        return go;
    }

    private static T FindProxy<T>(string path) where T : UdonSharpBehaviour
    {
        var go = GameObject.Find(path);
        if (go == null) return null;
        var udon = go.GetComponent<VRC.Udon.UdonBehaviour>();
        if (udon == null) return null;
        return UdonSharpEditorUtility.GetProxyBehaviour(udon) as T;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(target, value);
    }

    /// <summary>GUID からアセットをロードする。見つからない場合は null を返す。</summary>
    private static T LoadAssetByGuid<T>(string guid) where T : Object
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<T>(path);
    }

    private static string GetAssetDirectoryByGuid(string guid, string fallbackPath)
    {
        string assetPath = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(assetPath))
            return fallbackPath;

        int slash = assetPath.LastIndexOf('/');
        return slash > 0 ? assetPath.Substring(0, slash) : fallbackPath;
    }

    private static void EnsureDirectory(string assetPath)
    {
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Application.dataPath, "../" + assetPath)));
    }

    // ----------------------------------------------------------------
    // マテリアル生成ヘルパー (Standard シェーダー)
    // ----------------------------------------------------------------

    /// <summary>Standard シェーダー、Opaque モード。</summary>
    /// <param name="guid">アセットの GUID（既存アセットの検索に使用）</param>
    /// <param name="fallbackPath">GUID で見つからない場合の生成先パス</param>
    private static Material GetOrCreateStandardMat(
        string guid, string fallbackPath, Color color, float metallic, float smoothness)
    {
        Material mat = LoadAssetByGuid<Material>(guid);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", smoothness);
        AssetDatabase.CreateAsset(mat, fallbackPath);
        return mat;
    }

    /// <summary>Standard シェーダー、Fade モード（床インジケータ等）。</summary>
    /// <param name="guid">アセットの GUID（既存アセットの検索に使用）</param>
    /// <param name="fallbackPath">GUID で見つからない場合の生成先パス</param>
    private static Material GetOrCreateStandardFadeMat(
        string guid, string fallbackPath, Color color, float metallic, float smoothness)
    {
        Material mat = LoadAssetByGuid<Material>(guid);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Standard"));
        mat.SetFloat("_Mode", 2f); // Fade
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
        mat.color = color;
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", smoothness);
        AssetDatabase.CreateAsset(mat, fallbackPath);
        return mat;
    }

    /// <summary>Standard シェーダー、Opaque + Emission（ビーコンランプ向け）。</summary>
    /// <param name="guid">アセットの GUID（既存アセットの検索に使用）</param>
    /// <param name="fallbackPath">GUID で見つからない場合の生成先パス</param>
    private static Material GetOrCreateStandardEmissiveMat(
        string guid, string fallbackPath, Color color, Color emission, float smoothness)
    {
        Material mat = LoadAssetByGuid<Material>(guid);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Glossiness", smoothness);
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", emission);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        AssetDatabase.CreateAsset(mat, fallbackPath);
        return mat;
    }

    // ----------------------------------------------------------------
    // ジオメトリ生成ヘルパー
    // ----------------------------------------------------------------

    private static Mesh _cachedCubeMesh;
    private static Mesh _cachedSphereMesh;

    private static Mesh GetBuiltinMesh(string name)
    {
        if (name == "Cube")
        {
            if (_cachedCubeMesh == null)
                _cachedCubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return _cachedCubeMesh;
        }
        if (name == "Sphere")
        {
            if (_cachedSphereMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _cachedSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(tmp);
            }
            return _cachedSphereMesh;
        }
        return null;
    }

    /// <summary>コライダーなしの Cube パネルを作成する。</summary>
    private static GameObject CreateCubePanel(
        GameObject parent, string name, Material mat,
        Vector3 localPos, Vector3 localScale)
    {
        var go = CreateChild(parent, name);
        go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        return go;
    }
}
#endif
