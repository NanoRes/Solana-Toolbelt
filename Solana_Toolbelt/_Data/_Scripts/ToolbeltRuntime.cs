using System;
using System.Collections.Generic;
using UnityEngine;
using Solana.Unity.SDK;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Toolbelt;

[DisallowMultipleComponent]
public sealed class ToolbeltRuntime : MonoBehaviour
{
    private static ToolbeltRuntime instance;

    [Header("Configuration")]
    [SerializeField]
    private SolanaConfiguration solanaConfig;

    [Header("Toolbelt Services")]
    [SerializeField]
    private WalletManager walletManager;

    [SerializeField]
    private SolanaNftAccessManager solanaNftAccessManager;

    [SerializeField]
    private MonoBehaviour jsonUploaderBehaviour;

    [SerializeField]
    private MonoBehaviour uiBridgeBehaviour;

    private IToolbeltUiBridge uiBridge;

    [SerializeField]
    private MonoBehaviour nftStorageUploaderBehaviour;

    [SerializeField]
    private MonoBehaviour storageServiceBehaviour;

    [SerializeField]
    private ScriptableObject pricingDataAsset;

    public static ToolbeltRuntime Instance => instance;

    public SolanaConfiguration Configuration => solanaConfig;

    public IToolbeltServiceProvider ServiceProvider => solanaConfig != null ? solanaConfig.ServiceProvider : null;

    public static IToolbeltServiceProvider Services => Instance?.ServiceProvider;

    public ISolanaStorageService StorageService => storageService;

    private ISolanaStorageService storageService;
    private Web3 resolvedWeb3;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[ToolbeltRuntime] Duplicate runtime detected. Disabling this instance.");
            enabled = false;
            return;
        }

        instance = this;

        solanaConfig = ResolveSolanaConfiguration(solanaConfig);
        walletManager = ResolveComponent(walletManager);
        solanaNftAccessManager = ResolveComponent(solanaNftAccessManager);

        EnsureWeb3Instance();

        if (solanaConfig == null)
        {
            Debug.LogError("[ToolbeltRuntime] Missing SolanaConfiguration reference.");
            return;
        }

        ConfigureWeb3FromConfig();

        var jsonUploader = ResolveInterface<ILevelJsonUploader>(jsonUploaderBehaviour, nameof(jsonUploaderBehaviour));
        var nftStorageUploader = ResolveInterface<INftStorageUploader>(nftStorageUploaderBehaviour, nameof(nftStorageUploaderBehaviour));
        uiBridge = ResolveInterface<IToolbeltUiBridge>(uiBridgeBehaviour, nameof(uiBridgeBehaviour));
        storageService = ResolveInterface<ISolanaStorageService>(storageServiceBehaviour, nameof(storageServiceBehaviour));

        if (storageService == null)
        {
            if (!TryGetComponent(out UnityPersistentStorageService defaultStorage))
            {
                defaultStorage = gameObject.AddComponent<UnityPersistentStorageService>();
            }

            storageService = defaultStorage;
        }

        var missingDependencies = new List<string>();
        if (walletManager == null) missingDependencies.Add(nameof(walletManager));
        if (solanaNftAccessManager == null) missingDependencies.Add(nameof(solanaNftAccessManager));
        if (jsonUploader == null) missingDependencies.Add(nameof(ILevelJsonUploader));
        if (uiBridge == null) missingDependencies.Add(nameof(IToolbeltUiBridge));
        if (storageService == null) missingDependencies.Add(nameof(ISolanaStorageService));

        if (missingDependencies.Count > 0)
        {
            Debug.LogError($"[ToolbeltRuntime] Missing toolbelt dependencies: {string.Join(", ", missingDependencies)}");
            return;
        }

        solanaConfig.InitializeToolbeltServices(
            walletManager,
            solanaNftAccessManager,
            jsonUploader,
            uiBridge,
            storageService,
            nftStorageUploader);

        solanaConfig.ApplyRpcRateLimit();

        var pricingData = ResolvePricingDataAsset();
        if (pricingData != null)
        {
            solanaConfig.SetLevelPricingData(pricingData);
        }
        else if (pricingDataAsset != null)
        {
            Debug.LogError($"[ToolbeltRuntime] Assigned pricing data asset '{pricingDataAsset.name}' does not implement {nameof(ILevelPricingData)}.");
        }
    }

    private SolanaConfiguration ResolveSolanaConfiguration(SolanaConfiguration current)
    {
        if (current != null)
        {
            return current;
        }

        var loaded = Resources.Load<SolanaConfiguration>("Solana_Configuration");
        if (loaded != null)
        {
            return loaded;
        }

        var discovered = Resources.FindObjectsOfTypeAll<SolanaConfiguration>();
        if (discovered != null && discovered.Length > 0)
        {
            return discovered[0];
        }

        return null;
    }

    private void EnsureWeb3Instance()
    {
        if (resolvedWeb3 != null)
        {
            return;
        }

        resolvedWeb3 = Web3.Instance;

        if (resolvedWeb3 == null)
        {
            if (!TryGetComponent(out resolvedWeb3))
            {
#if UNITY_2023_1_OR_NEWER
                resolvedWeb3 = FindFirstObjectByType<Web3>(FindObjectsInactive.Include);
#else
                resolvedWeb3 = FindObjectOfType<Web3>();
#endif
            }
        }

        if (resolvedWeb3 == null)
        {
            resolvedWeb3 = gameObject.AddComponent<Web3>();
        }
    }

    private void ConfigureWeb3FromConfig()
    {
        if (resolvedWeb3 == null)
        {
            EnsureWeb3Instance();
        }

        if (resolvedWeb3 == null || solanaConfig == null)
        {
            return;
        }

        try
        {
            List<string> orderedRpcUrls = solanaConfig.GetOrderedRpcUrls();
            if (orderedRpcUrls != null && orderedRpcUrls.Count > 0)
            {
                string primaryRpc = orderedRpcUrls[0]?.Trim();
                if (!string.IsNullOrEmpty(primaryRpc))
                {
                    resolvedWeb3.rpcCluster = RpcCluster.MainNet;
                    resolvedWeb3.customRpc = primaryRpc;
                }
            }

            if (solanaConfig.streamingRpcUrls != null && solanaConfig.streamingRpcUrls.Length > 0)
            {
                string primaryWs = solanaConfig.streamingRpcUrls[0]?.Trim();
                if (!string.IsNullOrEmpty(primaryWs))
                {
                    resolvedWeb3.webSocketsRpc = primaryWs;
                }
            }

            resolvedWeb3.autoConnectOnStartup = false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ToolbeltRuntime] Failed to configure Web3 endpoints: {ex.Message}");
        }
    }

    private T ResolveComponent<T>(T current) where T : Component
    {
        if (current != null)
        {
            return current;
        }

        if (TryGetComponent(out T component))
        {
            return component;
        }

        component = GetComponentInChildren<T>(true);
        if (component != null)
        {
            return component;
        }

        component = GetComponentInParent<T>();
        if (component != null)
        {
            return component;
        }

        return FindFirstObjectByType<T>();
    }

    private TInterface ResolveInterface<TInterface>(Component assigned, string fieldName) where TInterface : class
    {
        if (assigned != null)
        {
            if (assigned is TInterface typed)
            {
                return typed;
            }

            Debug.LogError($"[ToolbeltRuntime] Assigned component on field '{fieldName}' does not implement {typeof(TInterface).Name}.");
            return null;
        }

        if (TryGetInterfaceFromComponents(GetComponents<MonoBehaviour>(), out TInterface local))
        {
            return local;
        }

        if (TryGetInterfaceFromComponents(GetComponentsInChildren<MonoBehaviour>(true), out TInterface child))
        {
            return child;
        }

        if (TryGetInterfaceFromComponents(GetComponentsInParent<MonoBehaviour>(true), out TInterface parent))
        {
            return parent;
        }

        var allBehaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var behaviour in allBehaviours)
        {
            if (behaviour is TInterface match)
            {
                return match;
            }
        }

        return null;
    }

    private bool TryGetInterfaceFromComponents<TInterface>(IEnumerable<MonoBehaviour> behaviours, out TInterface match) where TInterface : class
    {
        foreach (var behaviour in behaviours)
        {
            if (behaviour is TInterface typed)
            {
                match = typed;
                return true;
            }
        }

        match = null;
        return false;
    }

    private ILevelPricingData ResolvePricingDataAsset()
    {
        if (pricingDataAsset is ILevelPricingData assignedPricing)
        {
            return assignedPricing;
        }

        if (pricingDataAsset != null)
        {
            return null;
        }

        var discoveredAssets = Resources.FindObjectsOfTypeAll<ScriptableObject>();
        foreach (var asset in discoveredAssets)
        {
            if (asset is ILevelPricingData pricing)
            {
                return pricing;
            }
        }

        return null;
    }
}
