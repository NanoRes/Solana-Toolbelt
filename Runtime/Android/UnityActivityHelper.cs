using System;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR

/// <summary>
/// Helper utilities for retrieving the current Android activity from Unity.
/// Recent Unity releases store the activity reference in a WeakReference. This
/// helper abstracts that detail and provides a safe way to access the activity
/// from managed code.
/// </summary>
internal static class UnityActivityHelper
{
    private const string UnityPlayerClass = "com.unity3d.player.UnityPlayer";
    private const string GetCurrentActivityMethod = "getCurrentActivity";
    private const string GetActivityMethod = "getActivity";
    private const string GetUnityPlayerActivityMethod = "getUnityPlayerActivity";
    private const string CurrentActivityField = "currentActivity";
    private const string ActivityField = "activity";
    private const string CurrentActivityWeakReferenceField = "currentActivityWeakReference";
    private const string ActivityWeakReferenceField = "activityWeakReference";
    private const string JavaModifierClass = "java.lang.reflect.Modifier";
    private const string ActivityClassName = "android.app.Activity";
    private const string WeakReferenceClassName = "java.lang.ref.WeakReference";
    private const string WeakReferenceGetMethod = "get";

    private static readonly string[] ActivityMethodCandidates =
    {
        GetCurrentActivityMethod,
        GetActivityMethod,
        GetUnityPlayerActivityMethod,
    };

    private static readonly string[] ActivityFieldCandidates =
    {
        CurrentActivityField,
        ActivityField,
    };

    private static readonly string[] WeakReferenceFieldCandidates =
    {
        CurrentActivityWeakReferenceField,
        ActivityWeakReferenceField,
    };

    public static AndroidJavaObject GetCurrentActivity()
    {
        try
        {
            using (var unityPlayer = new AndroidJavaClass(UnityPlayerClass))
            {
                foreach (var methodName in ActivityMethodCandidates)
                {
                    var activity = TryCallActivityGetter(unityPlayer, methodName);
                    if (activity != null)
                        return activity;
                }

                var reflectedActivity = TryGetActivityViaReflection(unityPlayer);
                if (reflectedActivity != null)
                    return reflectedActivity;

                foreach (var fieldName in ActivityFieldCandidates)
                {
                    var activity = TryGetActivityField(unityPlayer, fieldName);
                    if (activity != null)
                        return activity;
                }

                foreach (var fieldName in WeakReferenceFieldCandidates)
                {
                    var activity = TryGetWeakReferenceActivity(unityPlayer, fieldName);
                    if (activity != null)
                        return activity;
                }
            }
        }
        catch (AndroidJavaException ex)
        {
            Debug.LogWarning($"[UnityActivityHelper] Failed to retrieve current activity via methods [{string.Join(", ", ActivityMethodCandidates)}] or fields [{string.Join(", ", ActivityFieldCandidates)}] / weak refs [{string.Join(", ", WeakReferenceFieldCandidates)}]: {ex.Message}");
        }

        return null;
    }

    private static AndroidJavaObject TryGetActivityViaReflection(AndroidJavaClass unityPlayer)
    {
        try
        {
            var fields = unityPlayer.Call<AndroidJavaObject[]>("getDeclaredFields");
            if (fields == null || fields.Length == 0)
                return null;

            using (var modifierClass = new AndroidJavaClass(JavaModifierClass))
            using (var activityClass = new AndroidJavaClass(ActivityClassName))
            using (var weakReferenceClass = new AndroidJavaClass(WeakReferenceClassName))
            {
                foreach (var field in fields)
                {
                    if (field == null)
                        continue;

                    using (field)
                    {
                        var modifiers = field.Call<int>("getModifiers");
                        var isStatic = modifierClass.CallStatic<bool>("isStatic", modifiers);
                        if (!isStatic)
                            continue;

                        using (var type = field.Call<AndroidJavaObject>("getType"))
                        {
                            if (IsTypeOrSubclass(type, activityClass, ActivityClassName))
                            {
                                field.Call("setAccessible", true);
                                var activity = field.Call<AndroidJavaObject>("get", new object[] { null });
                                if (activity != null)
                                    return activity;
                            }
                            else if (IsTypeOrSubclass(type, weakReferenceClass, WeakReferenceClassName))
                            {
                                field.Call("setAccessible", true);
                                using (var weakReference = field.Call<AndroidJavaObject>("get", new object[] { null }))
                                {
                                    var activity = weakReference?.Call<AndroidJavaObject>(WeakReferenceGetMethod);
                                    if (activity != null)
                                        return activity;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (AndroidJavaException)
        {
            // Reflection path failed; fall back to legacy field names.
        }

        return null;
    }

    private static AndroidJavaObject TryCallActivityGetter(AndroidJavaClass unityPlayer, string methodName)
    {
        try
        {
            return unityPlayer.CallStatic<AndroidJavaObject>(methodName);
        }
        catch (AndroidJavaException)
        {
            return null;
        }
    }

    private static AndroidJavaObject TryGetActivityField(AndroidJavaClass unityPlayer, string fieldName)
    {
        try
        {
            return unityPlayer.GetStatic<AndroidJavaObject>(fieldName);
        }
        catch (AndroidJavaException)
        {
            return null;
        }
    }

    private static AndroidJavaObject TryGetWeakReferenceActivity(AndroidJavaClass unityPlayer, string fieldName)
    {
        try
        {
            using (var weakReference = unityPlayer.GetStatic<AndroidJavaObject>(fieldName))
            {
                return weakReference?.Call<AndroidJavaObject>(WeakReferenceGetMethod);
            }
        }
        catch (AndroidJavaException)
        {
            return null;
        }
    }

    private static bool IsTypeOrSubclass(AndroidJavaObject candidateType, AndroidJavaClass targetClass, string targetClassName)
    {
        if (candidateType == null)
            return false;

        try
        {
            var typeName = candidateType.Call<string>("getName");
            if (typeName == targetClassName)
                return true;

            if (targetClass == null)
                return false;

            return targetClass.Call<bool>("isAssignableFrom", candidateType);
        }
        catch (AndroidJavaException)
        {
            return false;
        }
    }
}

#else

internal static class UnityActivityHelper
{
    public static AndroidJavaObject GetCurrentActivity() => null;
}

#endif
