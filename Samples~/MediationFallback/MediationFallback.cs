using System;
using FlightmodeSdk;
using UnityEngine;

// Generic fallback pattern: ask the mediation first; when it cannot fill, show Flightmode, which
// records why. Replace the two mediation delegates with your mediation SDK calls.
public sealed class MediationFallback : MonoBehaviour
{
    [Header("From get_sdk_config")]
    [SerializeField] string appKey;
    [SerializeField] string interstitialPlacementId;
    [SerializeField] string bannerPlacementId;

    [Header("Shown when an expired flight asks the player to connect")]
    [SerializeField] string connectMessage = "Connect to the internet to keep playing.";

    public Func<bool> MediationInterstitialReady = () => false;
    public Action<Action> ShowMediationInterstitial = done => done();

    void Start()
    {
        Flightmode.Initialize(new FlightmodeOptions { AppKey = appKey });
    }

    // Call at the natural break where the game shows an interstitial.
    public void ShowInterstitial(Action onDone)
    {
        if (MediationInterstitialReady())
        {
            ShowMediationInterstitial(onDone);
            return;
        }
        if (!Flightmode.Interstitial.Show(interstitialPlacementId, onDone)) onDone();
    }

    // Call at a level start to apply the server-assigned expired-flight policy.
    public void CheckExpiredFlight(Action onContinue)
    {
        if (!Flightmode.ShowConnectPrompt(connectMessage, onContinue)) onContinue();
    }

    public void ShowBannerFallback()
    {
        Flightmode.Banner.Show(bannerPlacementId, BannerPosition.Bottom);
    }

    public void HideBannerFallback()
    {
        Flightmode.Banner.Hide();
    }
}
