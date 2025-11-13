#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using Solana.Unity.Toolbelt;

namespace Solana.Unity.Toolbelt.Editor
{
    /// <summary>
    /// Simple window to input and store a private key for editor-only wallet testing.
    /// The key is persisted using <see cref="EditorPrefs"/> and never included in builds.
    /// </summary>
    public class DevWalletWindow : EditorWindow
    {
        private string _privateKey;

        [MenuItem("Solana/Dev Wallet Settings")]
        public static void ShowWindow()
        {
            GetWindow<DevWalletWindow>("Dev Wallet");
        }

        private void OnEnable()
        {
            string storedKey = EditorWalletDevTools.PrivateKey;
            if (EditorWalletDevTools.TryNormalizePrivateKey(storedKey, out string normalized, out _))
            {
                if (!string.Equals(storedKey, normalized, StringComparison.Ordinal))
                {
                    EditorWalletDevTools.PrivateKey = normalized;
                    storedKey = normalized;
                }
            }

            _privateKey = storedKey;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Editor Wallet Private Key", EditorStyles.boldLabel);
            _privateKey = EditorGUILayout.TextField("Private Key", _privateKey);

            GUILayout.Space(10);
            if (GUILayout.Button("Save"))
            {
                if (EditorWalletDevTools.TryNormalizePrivateKey(_privateKey, out string normalized, out string error))
                {
                    if (!string.Equals(EditorWalletDevTools.PrivateKey, normalized, StringComparison.Ordinal))
                    {
                        EditorWalletDevTools.PrivateKey = normalized;
                    }
                    Close();
                }
                else
                {
#if UNITY_EDITOR
                    EditorUtility.DisplayDialog(
                        "Invalid Private Key",
                        string.IsNullOrEmpty(error) ? "The provided private key could not be parsed." : error,
                        "OK");
#endif
                }
            }
        }
    }
}
#endif
