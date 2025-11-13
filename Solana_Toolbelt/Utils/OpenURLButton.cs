using UnityEngine;
using UnityEngine.UI;

namespace Solana.Unity.Toolbelt
{
    [RequireComponent(typeof(Button))]
    public class OpenURLButton : MonoBehaviour
    {
        [SerializeField] private string url = "https://solana.com";

        public void UserPressedButton()
        {
            if(string.IsNullOrEmpty(url))
            {
                Debug.LogWarning("URL is not set.");
                return;
            }

            Application.OpenURL(url);
        }
    }
}
