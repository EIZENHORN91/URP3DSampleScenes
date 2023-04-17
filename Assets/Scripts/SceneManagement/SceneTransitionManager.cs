using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public class SceneTransitionManager : MonoBehaviour
{
    public bool OverrideTransition;
    [Range(0, 1)] public float ManualTransition;

    public float m_TransitionTime;

    private Camera m_MainCamera;
    private Camera m_ScreenCamera;
    private CharacterController m_Player;
    private PersistentPlayer m_CameraManager;

    private bool m_InitialSceneLoad;

    private static SceneTransitionManager instance;

    [SerializeField] private LayerMask locationLayer;
    [SerializeField] private LayerMask hubLayer;
    private bool InHub = true;
    private ScreenController m_Screen;

    private SceneLoader m_Loader;

    private Transform spawnTransform;
    
    private Vector3 m_PositionAtLock;
    private Transform m_ParentAtLock;

    private bool InTransition = false;
    private bool CoolingOff = false; //After teleporting
    private float ElapsedTimeInTransition = 0;

    private Dictionary<string, SceneMetaData> registeredScenes;
    private SceneMetaData screenScene;
    private SceneMetaData currentScene;

    private Vector3 m_CameraPosition;
    private Quaternion m_CameraRotation;
    
    //Used for cinemachine transition
    private MediaSceneLoader m_MediaSceneLoader;

    void Awake()
    {
        //Kill if already exists
        if (instance != null)
        {
            Destroy(gameObject);
            return;
        }

        //Declare singleton
        instance = this;
        DontDestroyOnLoad(gameObject);
        
        m_Player = GameObject.Find("PlayerCapsule")?.GetComponent<CharacterController>(); //TODO: Don't hardcode string
        if (m_Player == null)
        {
            Debug.Log("Couldn't find character controller");
        }

        m_CameraManager = m_Player.transform.parent.GetComponent<PersistentPlayer>();
            
        m_MainCamera = GameObject.FindGameObjectWithTag("MainCamera")?.GetComponent<Camera>();

        if (m_MainCamera == null)
        {
            Debug.Log("Couldn't find Main Camera");
        }

        m_ScreenCamera = GameObject.FindGameObjectWithTag("ScreenCamera").GetComponent<Camera>();
        if (m_ScreenCamera == null)
        {
            Debug.Log("Couldn't find Screen Camera");
        }

        //Setup initial state
        InHub = true;
        m_InitialSceneLoad = true;

        registeredScenes = new Dictionary<string, SceneMetaData>();
        
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
    }

    void Update()
    {
        float t = OverrideTransition ? ManualTransition : ElapsedTimeInTransition / m_TransitionTime;

        if (InTransition)
        {
            ElapsedTimeInTransition += Time.deltaTime;

            if (ElapsedTimeInTransition > m_TransitionTime)
            {
                InTransition = false;
                
                if (m_Loader != null)
                {
                    m_Loader.SetCurrentVolumeWeight(1);
                }

                if (m_MediaSceneLoader) //check this some other way
                {
                    CinemachineTeleport();
                }
                else
                {
                    Teleport();
                }
                
                m_Loader = null;
                CoolingOff = true;
            }
            
            ElapsedTimeInTransition = Mathf.Min(m_TransitionTime, ElapsedTimeInTransition);
        }
        else
        {
            ElapsedTimeInTransition -= Time.deltaTime * 3;
            
            if (ElapsedTimeInTransition < 0 && CoolingOff)
            {
                CoolingOff = false;
            }

            ElapsedTimeInTransition = Mathf.Max(0, ElapsedTimeInTransition);
        }

        if (m_Loader != null && !CoolingOff)
        {
            float tSquared = t * t;
            m_Loader.SetCurrentVolumeWeight(1 - tSquared);
        }

        Shader.SetGlobalFloat("_TransitionAmount", t);
    }

    
    //TODO: Refactor this into calls to a function: SetupRenderSettings(SceneMetaData scene);
    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        bool isMainCamera = camera.CompareTag("MainCamera");
        
        //Toggle main light
        ToggleMainLight(currentScene, isMainCamera);
        ToggleMainLight(screenScene, !isMainCamera);
        
        //Setup render settings
        SceneMetaData sceneToRender = isMainCamera ? currentScene : screenScene;
        RenderSettings.fog = sceneToRender.FogEnabled;
        RenderSettings.skybox = sceneToRender.skybox;
        if (sceneToRender.reflection != null)
        {
            RenderSettings.customReflectionTexture = sceneToRender.reflection;
        }

        if (!isMainCamera)
        {
            camera.GetComponent<OverlayPosition>().UpdateWithOffset();
        }
    }

    private void ToggleMainLight(SceneMetaData scene, bool value)
    {
        if (scene != null && scene.mainLight != null)
        {
            scene.mainLight.SetActive(value);
        }
    }

    public static void CinemachineTeleport()
    {
        instance.InHub = !instance.InHub;
        instance.UpdateCullingMasks();

        Transform flythroughRoot = instance.m_MediaSceneLoader.transform;

        if (!instance.InHub)
        {
            flythroughRoot.position = instance.m_ScreenCamera.GetComponent<OverlayPosition>().GetOffset();
            instance.m_MediaSceneLoader.GetHubSceneLoader().SetCurrentVolume(instance.m_Loader.GetDestinationVolume());
            instance.screenScene.HubLoader.SetActive(true);
        }
        else
        {
            flythroughRoot.position = Vector3.zero;
            instance.m_Loader = instance.m_MediaSceneLoader.GetHubSceneLoader();
        }

        (instance.screenScene, instance.currentScene) = (instance.currentScene, instance.screenScene);
    }

    public static void Teleport()
    {
        if (!instance.currentScene || !instance.screenScene)
        {
            Debug.LogError("Can't teleport without two scenes enabled");
        }
        
        instance.InHub = !instance.InHub;
        
        instance.UpdateCullingMasks();

        //Swap Camera positions
        Vector3 cameraPlayerOffset = instance.m_MainCamera.transform.position - instance.m_Player.transform.position;

        OverlayPosition oc = instance.m_ScreenCamera.GetComponent<OverlayPosition>();

        Transform playerTransform = instance.m_Player.transform;

        StarterAssets.FirstPersonController controller = playerTransform.GetComponent<StarterAssets.FirstPersonController>();
        controller.enabled = false;

        if (instance.screenScene.CameraLockTransform == null) //TODO: Handle this for cockpit
        {
            playerTransform.position = instance.m_ScreenCamera.transform.position - cameraPlayerOffset;
            oc.ToggleOffset();
            
            if (instance.currentScene.CameraLockTransform != null)
            {
                playerTransform.position = instance.m_PositionAtLock;
                playerTransform.parent = instance.m_ParentAtLock;
                playerTransform.localRotation = Quaternion.identity;
                playerTransform.GetChild(0).localRotation = Quaternion.identity;
            }
            
            instance.m_Player.enabled = true;

            if (instance.screenScene.Director != null)
            {
                instance.m_CameraManager.FlythroughDirector = instance.screenScene.Director;
            }
            
            instance.m_MainCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = true; //see same line in the locked transform case
        }
        else
        {
            instance.m_PositionAtLock = playerTransform.position;
            instance.m_ParentAtLock = playerTransform.parent;
            
            Transform cameraLockTransform = instance.screenScene.CameraLockTransform;
            playerTransform.parent = cameraLockTransform;
            playerTransform.position = cameraLockTransform.position;
            playerTransform.rotation = cameraLockTransform.rotation;

            instance.m_MainCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = false; //TODO: this is hardcoded for the cockpit. Should probably be in the metadata

            instance.m_Player.enabled = false;
        }

        controller.enabled = true;

        SceneManager.SetActiveScene(instance.screenScene.Scene);
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;

        (instance.screenScene, instance.currentScene) = (instance.currentScene, instance.screenScene);

        if (instance.screenScene != null)
        {
            instance.SetHubLoaderAndDirector(instance.screenScene, false);
        }

        if (instance.currentScene != null)
        {
            instance.SetHubLoaderAndDirector(instance.currentScene, true);
        }
    }

    private void UpdateCullingMasks()
    {
        if (instance.InHub)
        {
            //Add to mask
            instance.m_MainCamera.cullingMask |= instance.hubLayer;
            instance.m_ScreenCamera.cullingMask |= instance.locationLayer;

            //Remove from mask
            instance.m_MainCamera.cullingMask ^= instance.locationLayer;
            instance.m_ScreenCamera.cullingMask ^= instance.hubLayer;
        }
        else
        {
            //Add to mask
            instance.m_MainCamera.cullingMask ^= instance.hubLayer;
            instance.m_ScreenCamera.cullingMask ^= instance.locationLayer;

            //Remove from mask
            instance.m_MainCamera.cullingMask |= instance.locationLayer;
            instance.m_ScreenCamera.cullingMask |= instance.hubLayer;
        }
    }

    private void SetHubLoaderAndDirector(SceneMetaData scene, bool isActive)
    {
        if (scene.HubLoader != null)
        {
            scene.HubLoader.SetActive(isActive);
        }

        if (scene.Director != null)
        {
            scene.Director.time = 0;
            scene.Director.enabled = isActive;
            if (isActive)
            {
                scene.Director.Play();
            }
            
        }
    }

    #region On Enable/Disable

    private void OnEnable()
    {
        //SceneManager.sceneLoaded += OnLocationFinishedLoading;
        //SceneManager.sceneUnloaded += OnLocationFinishedUnloading;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    #endregion

    #region Scene Loading

    public static void RegisterScene(string name, SceneMetaData metaData)
    {
        instance.registeredScenes.Add(name, metaData);

        if (instance.currentScene == null) //First loaded scene get's assigned to current
        {
            instance.currentScene = metaData;
        }
    }

    public static void EnableScene(SceneLoader sceneLoader)
    {
        SceneMetaData sceneMetaData = instance.registeredScenes[sceneLoader.SceneName];

        Debug.Log("Enabling this scene: " + sceneMetaData.Scene.name);

        instance.m_Loader = sceneLoader;
        instance.m_Screen = sceneLoader.screen;
        instance.m_InitialSceneLoad = false;
        //instance.m_ScreenCamera.GetComponent<ScreenCamera>().UpdateTarget();

        LightProbes.TetrahedralizeAsync();

        sceneMetaData.Root.SetActive(true);

        if (sceneMetaData.Director != null)
        {
            sceneMetaData.Director.time = sceneMetaData.DirectorStartTime;
            sceneMetaData.Director.Play();
        }

        if (sceneMetaData.SpawnTransform != null)
        {
            instance.m_ScreenCamera.GetComponent<OverlayPosition>().SetOffst(
                sceneMetaData.SpawnTransform.position - instance.m_Loader.ReferencePoint.position);
        }

        if (instance.m_Screen != null)
        {
            instance.m_Screen.TurnScreenOn();
        }

        instance.screenScene = sceneMetaData;
    }


    public static void DisableScene(SceneLoader sceneLoader)
    {
        SceneMetaData sceneMetaData = instance.registeredScenes[sceneLoader.SceneName];
        if (sceneMetaData == instance.currentScene)
        {
            Debug.Log("Trying to disable current scene");
            return;
        }

        Debug.Log("Disabling this scene: " + sceneMetaData.Scene.name);

        LightProbes.TetrahedralizeAsync();

        if (instance.m_Screen != null)
        {
            instance.m_Screen.TurnScreenOff(() => { sceneMetaData.Root.SetActive(false); });
        }
    }

    public static void StartTransition()
    {
        instance.InTransition = true;
    }

    public static void StartTransition(MediaSceneLoader mediaSceneLoader)
    {
        instance.m_MediaSceneLoader = mediaSceneLoader;
        if (!instance.InHub)
        {
            instance.m_Loader = instance.currentScene.HubLoader.GetComponentInChildren<SceneLoader>();
        }
        StartTransition();
    }

    public static void StopTransition()
    {
        instance.InTransition = false;
    }

    #endregion

    #region Getters

    public static bool IsLoaded(string sceneName)
    {
        return instance.registeredScenes.ContainsKey(sceneName);
    }

    public static RenderTexture GetScreenRT()
    {
        return instance.m_ScreenCamera.activeTexture;
    }

    public static bool HasNotLoadedScene()
    {
        return instance.m_InitialSceneLoad;
    }

    public static bool IsAvailable()
    {
        return instance != null;
    }

    public static GameObject GetMainCamera()
    {
        return instance.m_MainCamera.gameObject;
    }

    #endregion
}