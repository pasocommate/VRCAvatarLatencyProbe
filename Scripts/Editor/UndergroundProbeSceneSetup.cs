#if UNITY_EDITOR
using PasocomMate.VRCAvatarLatencyProbe;
using System.Reflection;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 地下落下方式レイテンシ計測ブースのシーン階層を構築するエディタヘルパー。
///
/// メニューバーから実行:
///   LatencyProbe > Setup Underground Booth
///
/// レイアウト (上から見た図):
///
///         +Z
///    ┌──────────┐
///    │          │
/// -X │ (シャフト) │ +X
///    │          │
///    └──────────┘
///         -Z
///   ↙ ButtonPole + DisplayPanel (角 -X, -Z)
/// </summary>
public static class UndergroundProbeSceneSetup
{
    private const string ROOT_NAME = "UndergroundLatencyProbe";

    // Canvas の論理解像度
    private const float CANVAS_WIDTH_PX  = 1200f;
    private const float CANVAS_HEIGHT_PX = 900f;
    private const float CANVAS_SCALE     = 0.001f;

    // シャフト寸法
    private const float SHAFT_HALF_W  = 1.1f;   // 外寸半幅
    private const float SHAFT_INNER   = 1.0f;   // 内寸半幅
    private const float WALL_THICK    = 0.1f;
    private const float SHAFT_DEPTH   = 27f;    // 15×1 モード対応

    // 扉
    private const float DOOR_THICK    = 0.08f;
    private const float DOOR_HALF_THICK = DOOR_THICK / 2f;

    // 枠
    private const float FRAME_W = 0.12f;
    private const float FRAME_H = 0.05f;

    // ポール
    private const float POLE_HEIGHT   = 1.0f;
    private const float POLE_RADIUS   = 0.04f;
    private const float POLE_OFFSET   = 1.5f;   // 角からのオフセット

    // ボタン
    private const float BUTTON_SIZE   = 0.12f;

    // 落下インジケータ
    private const int NUM_FALL_INDICATORS = 5;

    // アセット GUID 定数（各 .meta ファイルから取得）
    private const string GUID_MAT_SHAFT_WALL     = "4c6392d871c1779429b1ab530abc6e26";
    private const string GUID_MAT_DOOR_FRAME     = "d0da1c7640e87d548bd8ba6b3a5e00e7";
    private const string GUID_MAT_TRAPDOOR_PANEL = "43e619829db000049a290a35a5e7b568";
    private const string GUID_MAT_BUTTON_FACE    = "1e9f9c32e03946e41a37935e5eff2439";
    private const string GUID_MAT_BEACON_GREEN   = "dad2e652b07f83446b9ce1e6bcfec94d";
    private const string GUID_MAT_BEACON_RED     = "d6a92bc33a2b4474f99aafb6a4e5361d";
    private const string GUID_MESH_SHAFT         = "eb7f0bba2e65d924e9908fecf6b0cc87";
    private const string GUID_ANIM_CTRL_TRAPDOOR = "7f18cf2167e9ad64082053770641debf";
    private const string GUID_ANIM_IDLE          = "256cc7bcbebda6046902b92f46673ece";
    private const string GUID_ANIM_OPEN_CLOSE    = "4dece46b15b82d14a8fe190a5f090591";
    private const string GUID_UDON_BOOTH_BUTTON  = "3782d6a2c80db544ebf96a0e0e41c166";

    // ================================================================
    // メニュー項目
    // ================================================================

    [MenuItem("_PasocomMate-Dev/LatencyProbe/Setup Underground Booth")]
    private static void SetupBooth()
    {
        CreateObjects();
        WireReferences();
        Debug.Log("[LatencyProbe] 地下ブース セットアップ完了");
    }

    // ================================================================
    // オブジェクト生成
    // ================================================================

    private static void CreateObjects()
    {
        // 既存を削除
        GameObject existing;
        while ((existing = GameObject.Find(ROOT_NAME)) != null
               && existing.transform.parent == null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        GameObject root = new GameObject(ROOT_NAME);
        Undo.RegisterCreatedObjectUndo(root, "Setup Underground Booth");

        string matDir = GetAssetDirectoryByGuid(
            GUID_MAT_SHAFT_WALL,
            "Packages/tokyo.chigiri.pasocommate.vrcavatarlatencyprobe/Materials");
        string meshDir = GetAssetDirectoryByGuid(
            GUID_MESH_SHAFT,
            "Packages/tokyo.chigiri.pasocommate.vrcavatarlatencyprobe/Meshes");
        string animDir = GetAssetDirectoryByGuid(
            GUID_ANIM_CTRL_TRAPDOOR,
            "Packages/tokyo.chigiri.pasocommate.vrcavatarlatencyprobe/Animations");
        EnsureDirectory(matDir);
        EnsureDirectory(meshDir);
        EnsureDirectory(animDir);

        // マテリアル（GUID で既存を検索 → なければフォールバックパスで新規作成）
        Material wallMat = GetOrCreateStandardMat(GUID_MAT_SHAFT_WALL,
            matDir + "/ShaftWall.mat",
            new Color(0.35f, 0.35f, 0.38f, 1f), metallic: 0.05f, smoothness: 0.2f);
        Material doorFrameMat = GetOrCreateStandardMat(GUID_MAT_DOOR_FRAME,
            matDir + "/DoorFrame.mat",
            new Color(0.45f, 0.45f, 0.48f, 1f), metallic: 0.6f, smoothness: 0.7f);
        Material trapdoorMat = GetOrCreateStandardMat(GUID_MAT_TRAPDOOR_PANEL,
            matDir + "/TrapdoorPanel.mat",
            new Color(0.4f, 0.4f, 0.43f, 1f), metallic: 0.4f, smoothness: 0.5f);
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

        // ---- TeleportLatencyProbe ----
        GameObject probeGo = CreateChild(root, "TeleportLatencyProbe");
        UdonSharpComponentExtensions.AddUdonSharpComponent<TeleportLatencyProbe>(probeGo);

        // ---- FallStart (Y=0, 地表) ----
        GameObject fallStartGo = CreateChild(root, "FallStart");
        fallStartGo.transform.localPosition = new Vector3(0f, 0f, 0f);
        fallStartGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f);

        // ---- ExitPosition (計測終了後の帰還先、シャフト手前) ----
        GameObject exitGo = CreateChild(root, "ExitPosition");
        exitGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        exitGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f); // -Z（シャフトから離れる方向）を向く

        // ================================================================
        // シャフト (プロシージャルメッシュ: 壁4面 + 底面)
        // ================================================================

        GameObject shaftParent = CreateChild(root, "Shaft");
        GameObject shaftMeshGo = CreateChild(shaftParent, "ShaftMesh");

        Mesh shaftMesh = CreateShaftMesh();
        string meshPath = meshDir + "/ShaftMesh.asset";
        var existingMesh = LoadAssetByGuid<Mesh>(GUID_MESH_SHAFT);
        if (existingMesh != null)
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(GUID_MESH_SHAFT));
        AssetDatabase.CreateAsset(shaftMesh, meshPath);

        shaftMeshGo.AddComponent<MeshFilter>().sharedMesh = shaftMesh;
        shaftMeshGo.AddComponent<MeshRenderer>().sharedMaterial = wallMat;
        shaftMeshGo.AddComponent<MeshCollider>().sharedMesh = shaftMesh;

        // ================================================================
        // 地表フレーム
        // ================================================================

        float frameLen = SHAFT_HALF_W * 2f + FRAME_W * 2f;

        GameObject surfFrame = CreateChild(root, "SurfaceFrame");
        CreateCubePanel(surfFrame, "FrameNorth", doorFrameMat,
            new Vector3(0f, FRAME_H / 2f, SHAFT_HALF_W + FRAME_W / 2f),
            new Vector3(frameLen, FRAME_H, FRAME_W));
        CreateCubePanel(surfFrame, "FrameSouth", doorFrameMat,
            new Vector3(0f, FRAME_H / 2f, -(SHAFT_HALF_W + FRAME_W / 2f)),
            new Vector3(frameLen, FRAME_H, FRAME_W));
        CreateCubePanel(surfFrame, "FrameEast", doorFrameMat,
            new Vector3(SHAFT_HALF_W + FRAME_W / 2f, FRAME_H / 2f, 0f),
            new Vector3(FRAME_W, FRAME_H, SHAFT_HALF_W * 2f));
        CreateCubePanel(surfFrame, "FrameWest", doorFrameMat,
            new Vector3(-(SHAFT_HALF_W + FRAME_W / 2f), FRAME_H / 2f, 0f),
            new Vector3(FRAME_W, FRAME_H, SHAFT_HALF_W * 2f));

        // ================================================================
        // 観音開き扉 + Animator
        // ================================================================

        GameObject trapdoor = CreateChild(root, "Trapdoor");

        // FloorCollider (見えない BoxCollider)
        GameObject floorCol = CreateChild(trapdoor, "FloorCollider");
        floorCol.transform.localPosition = new Vector3(0f, -DOOR_HALF_THICK, 0f);
        BoxCollider col = floorCol.AddComponent<BoxCollider>();
        col.size = new Vector3(SHAFT_INNER * 2f, DOOR_THICK, SHAFT_INNER * 2f);

        // DoorPanelLeft (ピボット: X=-SHAFT_INNER)
        GameObject doorPivotL = CreateChild(trapdoor, "DoorPanelLeft");
        doorPivotL.transform.localPosition = new Vector3(-SHAFT_INNER, 0f, 0f);
        GameObject doorMeshL = CreateChild(doorPivotL, "DoorMesh");
        doorMeshL.transform.localPosition = new Vector3(SHAFT_INNER / 2f, -DOOR_HALF_THICK, 0f);
        doorMeshL.transform.localScale = new Vector3(SHAFT_INNER, DOOR_THICK, SHAFT_INNER * 2f);
        doorMeshL.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        doorMeshL.AddComponent<MeshRenderer>().sharedMaterial = trapdoorMat;

        // DoorPanelRight (ピボット: X=+SHAFT_INNER)
        GameObject doorPivotR = CreateChild(trapdoor, "DoorPanelRight");
        doorPivotR.transform.localPosition = new Vector3(SHAFT_INNER, 0f, 0f);
        GameObject doorMeshR = CreateChild(doorPivotR, "DoorMesh");
        doorMeshR.transform.localPosition = new Vector3(-SHAFT_INNER / 2f, -DOOR_HALF_THICK, 0f);
        doorMeshR.transform.localScale = new Vector3(SHAFT_INNER, DOOR_THICK, SHAFT_INNER * 2f);
        doorMeshR.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        doorMeshR.AddComponent<MeshRenderer>().sharedMaterial = trapdoorMat;

        // Animator Controller 生成
        var controller = CreateTrapdoorAnimator(animDir);
        var animator = trapdoor.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;

        // ================================================================
        // DisplayPanel (World Space Canvas)
        // ================================================================

        float dispY = 45f; // Y 回転 (シャフト中心を向く)
        float dispX = -(POLE_OFFSET + 0.3f);
        float dispZ = -(POLE_OFFSET + 0.3f);

        GameObject displayGo = CreateChild(root, "DisplayPanel");
        displayGo.transform.localPosition = new Vector3(dispX, 1.5f, dispZ);
        displayGo.transform.localEulerAngles = new Vector3(0f, dispY, 0f);

        Canvas canvas = displayGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRt = displayGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(CANVAS_WIDTH_PX, CANVAS_HEIGHT_PX);
        displayGo.transform.localScale = Vector3.one * CANVAS_SCALE;

        var scaler = displayGo.AddComponent<CanvasScaler>();
        scaler.scaleFactor = 1f;
        scaler.dynamicPixelsPerUnit = 1f;

        UdonSharpComponentExtensions.AddUdonSharpComponent<ProbeDisplay>(displayGo);

        // Background
        GameObject bgGo = CreateChild(displayGo, "Background");
        bgGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);
        RectTransform bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        // StatusText
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

        // LogText
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

        // TrialIndicators
        float indicatorSize = 35f;
        float indicatorSpacing = 55f;
        float totalW = NUM_FALL_INDICATORS * indicatorSpacing;
        float startX = -totalW / 2f + indicatorSpacing / 2f;

        for (int i = 0; i < NUM_FALL_INDICATORS; i++)
        {
            GameObject indGo = CreateChild(displayGo, $"TrialIndicator_{i}");
            indGo.AddComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f, 1f);
            RectTransform indRt = indGo.GetComponent<RectTransform>();
            indRt.anchorMin = new Vector2(0.5f, 0.80f);
            indRt.anchorMax = new Vector2(0.5f, 0.80f);
            indRt.sizeDelta = new Vector2(indicatorSize, indicatorSize);
            indRt.anchoredPosition = new Vector2(startX + i * indicatorSpacing, 0f);
        }

        // ================================================================
        // HUDPanel
        // ================================================================

        GameObject hudGo = CreateChild(root, "HUDPanel");
        hudGo.SetActive(false);

        Canvas hudCanvas = hudGo.AddComponent<Canvas>();
        hudCanvas.renderMode = RenderMode.WorldSpace;
        RectTransform hudRt = hudGo.GetComponent<RectTransform>();
        hudRt.sizeDelta = new Vector2(400f, 70f);
        hudGo.transform.localScale = Vector3.one * 0.001f;
        hudGo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;

        GameObject hudBgGo = CreateChild(hudGo, "HUDBackground");
        hudBgGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);
        var hudBgRt = hudBgGo.GetComponent<RectTransform>();
        hudBgRt.anchorMin = Vector2.zero;
        hudBgRt.anchorMax = Vector2.one;
        hudBgRt.offsetMin = Vector2.zero;
        hudBgRt.offsetMax = Vector2.zero;

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

        // ================================================================
        // ボタンポール (角 -X, -Z)
        // ================================================================

        float poleX = -POLE_OFFSET;
        float poleZ = -POLE_OFFSET;

        GameObject polePar = CreateChild(root, "ButtonPole");

        // Pole (Cylinder)
        GameObject poleGo = CreateChild(polePar, "Pole");
        poleGo.transform.localPosition = new Vector3(poleX, POLE_HEIGHT / 2f, poleZ);
        poleGo.transform.localScale = new Vector3(POLE_RADIUS * 2f, POLE_HEIGHT / 2f, POLE_RADIUS * 2f);
        poleGo.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cylinder");
        poleGo.AddComponent<MeshRenderer>().sharedMaterial = doorFrameMat;

        // 5×5 ボタンバックプレート (上段)
        float btnPanelW = 0.3f;
        float btnPanelH = 0.15f;
        float btn5x5Y = POLE_HEIGHT + 0.02f;
        float btn15x1Y = POLE_HEIGHT - 0.20f;

        CreateCubePanel(polePar, "ButtonBackplate5x5", doorFrameMat,
            new Vector3(poleX, btn5x5Y, poleZ),
            new Vector3(btnPanelW, btnPanelH, 0.02f),
            new Vector3(0f, 45f, 0f));

        // 15×1 ボタンバックプレート (下段)
        CreateCubePanel(polePar, "ButtonBackplate15x1", doorFrameMat,
            new Vector3(poleX, btn15x1Y, poleZ),
            new Vector3(btnPanelW, btnPanelH, 0.02f),
            new Vector3(0f, 45f, 0f));

        // BoothButton (5×5)
        float btnOffset5x5 = 0.025f;
        Vector3 btnFwd = Quaternion.Euler(0f, 45f, 0f) * Vector3.forward;
        GameObject btn5x5Go = CreateChild(root, "BoothButton5x5");
        btn5x5Go.transform.localPosition = new Vector3(poleX, btn5x5Y, poleZ) + btnFwd * btnOffset5x5;
        btn5x5Go.transform.localEulerAngles = new Vector3(0f, 45f, 0f);
        btn5x5Go.transform.localScale = new Vector3(BUTTON_SIZE, BUTTON_SIZE, 0.04f);
        btn5x5Go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        btn5x5Go.AddComponent<MeshRenderer>().sharedMaterial = buttonMat;
        btn5x5Go.AddComponent<BoxCollider>().isTrigger = false;

        // BoothButton (15×1)
        GameObject btn15x1Go = CreateChild(root, "BoothButton15x1");
        btn15x1Go.transform.localPosition = new Vector3(poleX, btn15x1Y, poleZ) + btnFwd * btnOffset5x5;
        btn15x1Go.transform.localEulerAngles = new Vector3(0f, 45f, 0f);
        btn15x1Go.transform.localScale = new Vector3(BUTTON_SIZE, BUTTON_SIZE, 0.04f);
        btn15x1Go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        btn15x1Go.AddComponent<MeshRenderer>().sharedMaterial = buttonMat;
        btn15x1Go.AddComponent<BoxCollider>().isTrigger = false;

        // ================================================================
        // StatusBeacon (ポール上)
        // ================================================================

        GameObject beaconGo = CreateChild(root, "StatusBeacon");
        beaconGo.transform.localPosition = new Vector3(poleX, POLE_HEIGHT + 0.15f, poleZ);
        beaconGo.transform.localEulerAngles = new Vector3(0f, 45f, 0f);
        beaconGo.transform.localScale = new Vector3(0.3f, 0.06f, 0.03f);
        beaconGo.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        beaconGo.AddComponent<MeshRenderer>().sharedMaterial = beaconGreenMat;

        // 配置
        root.transform.position = Vector3.zero;

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = root;

        Debug.Log("[LatencyProbe] 地下ブース オブジェクト生成完了");
    }

    // ================================================================
    // WireReferences
    // ================================================================

    private static void WireReferences()
    {
        var probeProxy = FindProxy<TeleportLatencyProbe>(ROOT_NAME + "/TeleportLatencyProbe");
        var displayProxy = FindProxy<ProbeDisplay>(ROOT_NAME + "/DisplayPanel");

        if (probeProxy == null || displayProxy == null)
        {
            Debug.LogError("[LatencyProbe] オブジェクトが見つかりません。");
            return;
        }

        var fallStart = GameObject.Find(ROOT_NAME + "/FallStart")?.transform;
        if (fallStart == null)
        {
            Debug.LogError("[LatencyProbe] FallStart が見つかりません");
            return;
        }

        // UI 参照
        var statusText = GameObject.Find(ROOT_NAME + "/DisplayPanel/StatusText")
            ?.GetComponent<TextMeshProUGUI>();
        var logText = GameObject.Find(ROOT_NAME + "/DisplayPanel/LogText")
            ?.GetComponent<TextMeshProUGUI>();
        var boothTf = GameObject.Find(ROOT_NAME)?.transform;
        var hudRootTf = boothTf?.Find("HUDPanel");
        var hudRoot = hudRootTf?.gameObject;
        var hudText = hudRootTf?.Find("HUDText")?.GetComponent<TextMeshProUGUI>();

        // ---- ProbeDisplay ----
        SetField(displayProxy, "_probe", probeProxy);
        SetField(displayProxy, "_statusText", statusText);
        SetField(displayProxy, "_logText", logText);
        SetField(displayProxy, "_hudRoot", hudRoot);
        SetField(displayProxy, "_hudText", hudText);

        // StatusBeacon
        var beaconTf = boothTf?.Find("StatusBeacon");
        if (beaconTf != null)
        {
            SetField(displayProxy, "_statusBeacon", beaconTf.GetComponent<MeshRenderer>());
            SetField(displayProxy, "_beaconGreenMat",
                LoadAssetByGuid<Material>(GUID_MAT_BEACON_GREEN));
            SetField(displayProxy, "_beaconRedMat",
                LoadAssetByGuid<Material>(GUID_MAT_BEACON_RED));
        }

        // トライアル進捗
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
        SetField(probeProxy, "_display", displayProxy);
        SetField(probeProxy, "_fallStart", fallStart);

        var exitPos = GameObject.Find(ROOT_NAME + "/ExitPosition")?.transform;
        if (exitPos != null)
            SetField(probeProxy, "_exitPosition", exitPos);

        // Trapdoor Animator + FloorCollider
        var trapdoorTf = boothTf?.Find("Trapdoor");
        if (trapdoorTf != null)
        {
            var anim = trapdoorTf.GetComponent<Animator>();
            if (anim != null)
                SetField(probeProxy, "_trapdoorAnimator", anim);

            var floorColGo = trapdoorTf.Find("FloorCollider")?.gameObject;
            if (floorColGo != null)
                SetField(probeProxy, "_trapdoorFloor", floorColGo);
        }

        UdonSharpEditorUtility.CopyProxyToUdon(probeProxy);

        // ---- BoothButton (5×5) ----
        WireBoothButton(ROOT_NAME + "/BoothButton5x5", probeProxy, false, "計測開始 (5×5)");

        // ---- BoothButton (15×1) ----
        WireBoothButton(ROOT_NAME + "/BoothButton15x1", probeProxy, true, "計測開始 (15×1)");

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[LatencyProbe] 地下ブース 参照ワイヤリング完了");
    }

    private static void WireBoothButton(
        string path, TeleportLatencyProbe probeProxy, bool singleFallMode, string interactionText)
    {
        var buttonGo = GameObject.Find(path);
        if (buttonGo == null)
        {
            Debug.LogError($"[LatencyProbe] {path} が見つかりません。");
            return;
        }

        var programAsset = LoadAssetByGuid<Object>(GUID_UDON_BOOTH_BUTTON);
        if (programAsset == null)
        {
            string fallbackPath = AssetDatabase.GUIDToAssetPath(GUID_UDON_BOOTH_BUTTON);
            if (string.IsNullOrEmpty(fallbackPath))
                fallbackPath = "(GUID: " + GUID_UDON_BOOTH_BUTTON + ")";
            Debug.LogError(
                "[LatencyProbe] BoothButton の U# プログラムアセットが見つかりません:\n" +
                "  " + fallbackPath + "\n" +
                "Tools > UdonSharp > Refresh All UdonSharp Programs を実行後、再度 Setup を実行してください。");
            return;
        }

        var buttonProxy = FindProxy<BoothButton>(path);
        if (buttonProxy == null)
            buttonProxy = UdonSharpComponentExtensions.AddUdonSharpComponent<BoothButton>(buttonGo);

        SetField(buttonProxy, "_probe", probeProxy);
        SetField(buttonProxy, "_singleFallMode", singleFallMode);
        UdonSharpEditorUtility.CopyProxyToUdon(buttonProxy);

        var udon = buttonGo.GetComponent<VRC.Udon.UdonBehaviour>();
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

    // ================================================================
    // シャフトメッシュ生成
    // ================================================================

    /// <summary>
    /// 壁4面の内面 + 底面の上面で構成されるメッシュを生成する。
    /// 壁の外面は地中に埋まるため不要。
    /// </summary>
    private static Mesh CreateShaftMesh()
    {
        float hw = SHAFT_HALF_W;
        float d = SHAFT_DEPTH;
        float t = WALL_THICK;

        // 4つの壁の内面 (各4頂点) + 底面の上面 (4頂点) = 20頂点
        // 各面は独立した頂点を持つ（法線を正しくするため）
        Vector3[] vertices = new Vector3[20];
        Vector3[] normals = new Vector3[20];
        Vector2[] uvs = new Vector2[20];
        int[] triangles = new int[30]; // 5 faces × 2 tri × 3 indices

        int vi = 0;
        int ti = 0;

        // 北壁 (+Z) 内面: 法線 = -Z
        AddQuad(vertices, normals, uvs, triangles, ref vi, ref ti,
            new Vector3(-hw + t, 0f,     hw - t),
            new Vector3( hw - t, 0f,     hw - t),
            new Vector3( hw - t, -d,     hw - t),
            new Vector3(-hw + t, -d,     hw - t),
            Vector3.back);

        // 南壁 (-Z) 内面: 法線 = +Z
        AddQuad(vertices, normals, uvs, triangles, ref vi, ref ti,
            new Vector3( hw - t, 0f,    -(hw - t)),
            new Vector3(-hw + t, 0f,    -(hw - t)),
            new Vector3(-hw + t, -d,    -(hw - t)),
            new Vector3( hw - t, -d,    -(hw - t)),
            Vector3.forward);

        // 東壁 (+X) 内面: 法線 = -X
        AddQuad(vertices, normals, uvs, triangles, ref vi, ref ti,
            new Vector3( hw - t, 0f,     hw - t),
            new Vector3( hw - t, 0f,    -(hw - t)),
            new Vector3( hw - t, -d,    -(hw - t)),
            new Vector3( hw - t, -d,     hw - t),
            Vector3.left);

        // 西壁 (-X) 内面: 法線 = +X
        AddQuad(vertices, normals, uvs, triangles, ref vi, ref ti,
            new Vector3(-(hw - t), 0f,   -(hw - t)),
            new Vector3(-(hw - t), 0f,    hw - t),
            new Vector3(-(hw - t), -d,    hw - t),
            new Vector3(-(hw - t), -d,   -(hw - t)),
            Vector3.right);

        // 底面: 法線 = +Y
        AddQuad(vertices, normals, uvs, triangles, ref vi, ref ti,
            new Vector3(-hw + t, -d,  hw - t),
            new Vector3( hw - t, -d,  hw - t),
            new Vector3( hw - t, -d, -(hw - t)),
            new Vector3(-hw + t, -d, -(hw - t)),
            Vector3.up);

        Mesh mesh = new Mesh();
        mesh.name = "ShaftMesh";
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddQuad(
        Vector3[] verts, Vector3[] norms, Vector2[] uvs, int[] tris,
        ref int vi, ref int ti,
        Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl,
        Vector3 normal)
    {
        int v0 = vi;
        verts[vi] = tl; norms[vi] = normal; uvs[vi] = new Vector2(0f, 1f); vi++;
        verts[vi] = tr; norms[vi] = normal; uvs[vi] = new Vector2(1f, 1f); vi++;
        verts[vi] = br; norms[vi] = normal; uvs[vi] = new Vector2(1f, 0f); vi++;
        verts[vi] = bl; norms[vi] = normal; uvs[vi] = new Vector2(0f, 0f); vi++;

        tris[ti++] = v0;
        tris[ti++] = v0 + 1;
        tris[ti++] = v0 + 2;
        tris[ti++] = v0;
        tris[ti++] = v0 + 2;
        tris[ti++] = v0 + 3;
    }

    // ================================================================
    // Animator Controller 生成
    // ================================================================

    private static AnimatorController CreateTrapdoorAnimator(string animDir)
    {
        string ctrlPath = animDir + "/TrapdoorAnimator.controller";
        var existingCtrl = LoadAssetByGuid<AnimatorController>(GUID_ANIM_CTRL_TRAPDOOR);
        if (existingCtrl != null)
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(GUID_ANIM_CTRL_TRAPDOOR));

        var controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        controller.AddParameter("Play", AnimatorControllerParameterType.Trigger);

        var rootSM = controller.layers[0].stateMachine;

        // ---- Idle (デフォルト: 閉じた状態) ----
        var idleState = rootSM.AddState("Idle", new Vector3(200, 0, 0));
        idleState.motion = CreateIdleClip(animDir);

        // ---- OpenClose (開閉一体アニメーション) ----
        var openCloseState = rootSM.AddState("OpenClose", new Vector3(450, 0, 0));
        openCloseState.motion = CreateOpenCloseClip(animDir);

        // ---- 遷移 ----
        // Idle → OpenClose (Play トリガー)
        var t1 = idleState.AddTransition(openCloseState);
        t1.AddCondition(AnimatorConditionMode.If, 0, "Play");
        t1.hasExitTime = false;
        t1.duration = 0f;

        // OpenClose → Idle (clip 終了)
        var t2 = openCloseState.AddTransition(idleState);
        t2.hasExitTime = true;
        t2.exitTime = 1f;
        t2.duration = 0f;

        rootSM.defaultState = idleState;

        AssetDatabase.SaveAssets();
        return controller;
    }

    /// <summary>1フレームの静止クリップ（閉じた状態維持）。</summary>
    private static AnimationClip CreateIdleClip(string animDir)
    {
        string path = animDir + "/TrapdoorIdle.anim";
        var existing = LoadAssetByGuid<AnimationClip>(GUID_ANIM_IDLE);
        if (existing != null)
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(GUID_ANIM_IDLE));

        var clip = new AnimationClip();
        clip.name = "TrapdoorIdle";

        clip.SetCurve("DoorPanelLeft", typeof(Transform), "localEulerAngles.z",
            AnimationCurve.Constant(0f, 0f, 0f));
        clip.SetCurve("DoorPanelRight", typeof(Transform), "localEulerAngles.z",
            AnimationCurve.Constant(0f, 0f, 0f));

        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    /// <summary>
    /// 開閉一体アニメーション。
    ///   0.0–0.3s : 開く（速い）
    ///   0.3–1.0s : 開いたまま保持
    ///   1.0–1.7s : 閉じる（ゆっくり）
    /// </summary>
    private static AnimationClip CreateOpenCloseClip(string animDir)
    {
        string path = animDir + "/TrapdoorOpenClose.anim";
        var existing = LoadAssetByGuid<AnimationClip>(GUID_ANIM_OPEN_CLOSE);
        if (existing != null)
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(GUID_ANIM_OPEN_CLOSE));

        var clip = new AnimationClip();
        clip.name = "TrapdoorOpenClose";

        const float tOpen  = 0.3f;   // 開き切る時刻
        const float tHold  = 1.0f;   // 閉じ始める時刻
        const float tClose = 1.7f;   // 閉じ切る時刻（クリップ全長）

        // DoorPanelLeft: Z 0 → -90 → -90 → 0
        clip.SetCurve("DoorPanelLeft", typeof(Transform), "localEulerAngles.z",
            new AnimationCurve(
                new Keyframe(0f,     0f),
                new Keyframe(tOpen, -90f),
                new Keyframe(tHold, -90f),
                new Keyframe(tClose,  0f)));

        // DoorPanelRight: Z 0 → 90 → 90 → 0
        clip.SetCurve("DoorPanelRight", typeof(Transform), "localEulerAngles.z",
            new AnimationCurve(
                new Keyframe(0f,    0f),
                new Keyframe(tOpen, 90f),
                new Keyframe(tHold, 90f),
                new Keyframe(tClose, 0f)));

        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    // ================================================================
    // ユーティリティ
    // ================================================================

    private static GameObject CreateChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        Undo.RegisterCreatedObjectUndo(go, "Setup Underground Booth");
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

    private static void EnsureDirectory(string assetPath)
    {
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Application.dataPath, "../" + assetPath)));
    }

    // ================================================================
    // マテリアル生成ヘルパー
    // ================================================================

    /// <summary>GUID で既存マテリアルを検索し、なければ fallbackPath に新規作成する。</summary>
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

    /// <summary>GUID で既存マテリアルを検索し、なければ fallbackPath に新規作成する（Fade モード）。</summary>
    private static Material GetOrCreateStandardFadeMat(
        string guid, string fallbackPath, Color color, float metallic, float smoothness)
    {
        Material mat = LoadAssetByGuid<Material>(guid);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Standard"));
        mat.SetFloat("_Mode", 2f);
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

    /// <summary>GUID で既存マテリアルを検索し、なければ fallbackPath に新規作成する（Emissive）。</summary>
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

    // ================================================================
    // GUID ベースアセットロード
    // ================================================================

    /// <summary>GUID からアセットをロードする。パスが解決できない場合は null を返す。</summary>
    private static T LoadAssetByGuid<T>(string guid) where T : UnityEngine.Object
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

    // ================================================================
    // ジオメトリ生成ヘルパー
    // ================================================================

    private static Mesh _cachedCubeMesh;
    private static Mesh _cachedCylinderMesh;

    private static Mesh GetBuiltinMesh(string name)
    {
        if (name == "Cube")
        {
            if (_cachedCubeMesh == null)
                _cachedCubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return _cachedCubeMesh;
        }
        if (name == "Cylinder")
        {
            if (_cachedCylinderMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _cachedCylinderMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(tmp);
            }
            return _cachedCylinderMesh;
        }
        return null;
    }

    private static GameObject CreateCubePanel(
        GameObject parent, string name, Material mat,
        Vector3 localPos, Vector3 localScale)
    {
        return CreateCubePanel(parent, name, mat, localPos, localScale, Vector3.zero);
    }

    private static GameObject CreateCubePanel(
        GameObject parent, string name, Material mat,
        Vector3 localPos, Vector3 localScale, Vector3 localEuler)
    {
        var go = CreateChild(parent, name);
        go.AddComponent<MeshFilter>().sharedMesh = GetBuiltinMesh("Cube");
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.transform.localEulerAngles = localEuler;
        return go;
    }
}
#endif
