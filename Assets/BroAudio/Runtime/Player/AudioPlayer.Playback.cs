using System;
using System.Collections;
using UnityEngine;
using Ami.Extension;
using static Ami.BroAudio.Utility;

namespace Ami.BroAudio.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>
    {
        [Obsolete]
        public event Action<SoundID> OnEndPlaying
        {
            add => _onEnd += value;
            remove => _onEnd -= value;
        }

        private PlaybackPreference _pref;
        private StopMode _stopMode = default;
        private Coroutine _playbackControlCoroutine = null;

        private event Action<SoundID> _onEnd = null;
        private event Action<IAudioPlayer> _onUpdate = null;
        private event Action<IAudioPlayer> _onStart = null;

        private bool _onUpdateContainsScheduledEndCheck = false;


        private AudioPlayer queuedAudioPlayer = null;

        public int PlaybackStartingTime { get; private set; }

        public void SetPlaybackData(SoundID id, PlaybackPreference pref)
        {
            ID = id;
            _pref = pref;
        }

        public void Play()
        {
            IsStopping = false; // just in case we're back in here before we properly stopped

            if (!ID.IsValid() || _pref.Entity == null)
            {
                return;
            }

            if(PlaybackStartingTime == 0)
            {
                PlaybackStartingTime = TimeExtension.UnscaledCurrentFrameBeganTime;
            }

            try
            {
                if (_stopMode == StopMode.Stop)
                {
                    _clip = _pref.PickNewClip();
                }

                this.StartCoroutineAndReassign(PlayControl(), ref _playbackControlCoroutine);
            }
            catch (Exception ex)
            {
                ClearEvents();
                EndPlaying();
                Debug.LogException(ex);
            }
        }

        private IEnumerator PlayControl()
        {
            if (!SoundManager.Instance.TryGetAudioTypePref(ID.ToAudioType(), out var audioTypePref))
            {
                Debug.LogError(LogTitle + $"The ID:{ID} is invalid");
                yield break;
            }

            if (!Mathf.Approximately(audioTypePref.Volume, DefaultTrackVolume) && !_audioTypeVolume.IsFading)
            {
                _audioTypeVolume.Complete(audioTypePref.Volume, false);
            }
            _clipVolume.Complete(0f, false);

#if PACKAGE_ADDRESSABLES
            // Wait for the addressable to finish loading if it's currently loading
            if (_clip is Data.BroAudioClip broAudioClip && broAudioClip.IsAddressablesAvailable())
            {
                if (!broAudioClip.IsLoaded) 
                {
                    if (broAudioClip.IsLoading)
                    {
                        // Wait for the existing loading operation to complete
                        yield return broAudioClip.GetCurrentOperationHandle();
                    }
                    else
                    {
                        // Start loading and wait for it no matter what the user has set
                        yield return broAudioClip.LoadAssetAsync();

                        if (SoundManager.Instance != null && !SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips) // but let them know if we had to
                        {
                            var loadedAudioClip = _clip.GetAudioClip();
                            if (loadedAudioClip != null)
                            {
                                Debug.LogWarning(LogTitle + $"Lazy loaded addressable audio clip {loadedAudioClip.name} for {ID}");
                            }
                            else
                            {
                                Debug.LogWarning(LogTitle + $"Failed to load addressable audio clip for {ID}");
                            }
                        }
                    }

                    // Update tracking when playback starts
                    if (SoundManager.Instance != null)
                    {
                        SoundManager.Instance.UpdateLoadedEntityLastPlayedTime(ID);
                    }
                }
            }
#endif

            var audioClip = _clip.GetAudioClip();
            int sampleRate = audioClip.frequency;
            bool hasScheduled = false;
            if (_stopMode == StopMode.Stop) // we only do this process when it's fresh
            {
                AudioSource.clip = audioClip;
                AudioSource.priority = _pref.Entity.Priority;

                SetPlayPosition(sampleRate);
                SetInitialPitch(_pref.Entity, audioTypePref);
                SetSpatial(_pref);

                if (IsDominator)
                {
                    TrackType = AudioTrackType.Dominator;
                }
                else
                {
                    SetTrackEffect(audioTypePref.EffectType, SetEffectMode.Add);
                }

                SetScheduleTime(out hasScheduled);
                if(hasScheduled)
                {
                    yield return WaitForScheduledStartTime();
                }

                if (_decorators.TryGetDecorator<MusicPlayer>(out var musicPlayer))
                {
                    AudioSource.reverbZoneMix = 0f;
                    AudioSource.priority = AudioConstant.HighestPriority;
                    musicPlayer.DoTransition(ref _pref);
                    while (musicPlayer.IsWaitingForTransition)
                    {
                        yield return null;
                    }
                }
#if !UNITY_WEBGL
                AudioTrack = MixerPool.GetTrack(TrackType);
#endif
            }

            if(!hasScheduled)
            {
                StartPlaying(sampleRate);
            }
            float targetClipVolume = _clip.Volume * _pref.Entity.GetMasterVolume();
            float elapsedTime = 0f;

            #region FadeIn
            if (_pref.HasFadeIn(_clip.FadeIn, out var fadeIn, out var fadeInEase))
            {
                _clipVolume.SetTarget(targetClipVolume);
                while (_clipVolume.Update(ref elapsedTime, fadeIn, fadeInEase))
                {
                    yield return null;
                    if (!OnUpdate())
                    {
                        yield break;
                    }
                }
            }
            else
            {
                _clipVolume.Complete(targetClipVolume);
            }
            #endregion

            if (_pref.IsLoop(LoopType.SeamlessLoop))
            {
                _pref.ScheduledStartTime = 0d;
                _pref.ApplySeamlessFade();
            }

            #region Play & Wait
            {
                bool queuedAudioPlayerStarted = false;
                double dspEndTime = double.NaN;
                double queuedAudioPlayerDSPTime = double.NaN;
                bool hasPlayed = false;
                bool hasFadeOut = false;
                float fadeOut;
                Ease fadeOutEase;

                while (true)
                {
                    int clipSamples = AudioSource.clip.samples;
                    int startSample = GetSample(sampleRate, _clip.StartPosition);
                    int endSample = clipSamples - GetSample(sampleRate, _clip.EndPosition);

                    int fadeOutSample = endSample;

                    hasFadeOut = false;

                    if (_pref.HasFadeOut(_clip.FadeOut, out fadeOut, out fadeOutEase))
                    {
                        fadeOutSample = endSample - Mathf.RoundToInt(fadeOut * sampleRate);
                        hasFadeOut = true;
                    }

                    var dspTime = AudioSettings.dspTime;
                    int currentSample = AudioSource.timeSamples;

                    if (_pref.IsLoop(LoopType.Loop))
                    {
                        hasFadeOut = false;
                    }

                    int samplesToEnd = endSample - currentSample;
                    int samplesToFadeOut = fadeOutSample - currentSample;

                    hasPlayed |= currentSample > startSample;

                    if (!double.IsNaN(dspEndTime) && dspTime > dspEndTime) // The queued player has started even if we were still telling it to wait
                        break;

                    bool isTimeToQueue;

                    if (_pref.IsLoop(LoopType.Loop) || !hasFadeOut)
                    {
                        isTimeToQueue = samplesToEnd <= sampleRate * 1.0;
                    }
                    else if (_pref.IsLoop(LoopType.SeamlessLoop))
                    {
                        isTimeToQueue = samplesToFadeOut <= sampleRate * 1.0;
                    }
                    else
                    {
                        isTimeToQueue = false;
                    }

                    if (hasPlayed && isTimeToQueue) // queue up the transition in the last second of the loop
                    {
                        dspEndTime = dspTime + (samplesToEnd / (double)sampleRate);
                        queuedAudioPlayerDSPTime = dspEndTime;

                        if (_pref.IsLoop(LoopType.SeamlessLoop) && hasFadeOut)
                            queuedAudioPlayerDSPTime = dspTime + (samplesToFadeOut / (double)sampleRate);

                        AudioSource.SetScheduledEndTime(dspEndTime);

                        if ((_pref.IsLoop(LoopType.Loop) || _pref.IsLoop(LoopType.SeamlessLoop) && (!_pref.IsChainedMode() || _pref.ChainedModeStage != PlaybackStage.End)))
                        {
                            if (queuedAudioPlayer == null)
                            {
                                queuedAudioPlayer = SoundManager.Instance.GetPooledAudioPlayer(ID, _instanceWrapper);
                                queuedAudioPlayer.OnStart(_ => // workaround so we assign the correct stuff but THEN delay the actual start to a PlayScheduled call
                                {
                                    queuedAudioPlayerStarted = true;
                                    queuedAudioPlayer.SetScheduledStartTime(queuedAudioPlayerDSPTime);
                                });
                                UpdateSecondaryAudioPlayer(queuedAudioPlayer);
                                queuedAudioPlayer.Play();
                            }
                            else
                            {
                                UpdateSecondaryAudioPlayer(queuedAudioPlayer);
                            }

                            if (queuedAudioPlayerStarted)
                            {
                                queuedAudioPlayer.SetScheduledStartTime(queuedAudioPlayerDSPTime);
                            }
                        }
                    }

                    if (hasPlayed)
                    {
                        if (currentSample <= startSample || currentSample >= endSample)
                            break;

                        if (_pref.IsLoop(LoopType.SeamlessLoop) && hasFadeOut && currentSample >= fadeOutSample) // transition at the start of fading
                            break;
                    }

                    yield return null;
                    if (!OnUpdate())
                    {
                        yield break;
                    }
                }

                if (queuedAudioPlayer != null) 
                {
                    TransferToSecondaryAudioPlayer(ref queuedAudioPlayer);
                }
                else if (_pref.IsLoop(LoopType.Loop) || _pref.IsLoop(LoopType.SeamlessLoop))
                {
                    Debug.LogError(LogTitle + "Queued audio player is null");
                }

                if (hasFadeOut)
                {
                    _clipVolume.SetTarget(0f);
                    elapsedTime = 0f;
                    while (_clipVolume.Update(ref elapsedTime, fadeOut, fadeOutEase))
                    {
                        yield return null;
                        if (!OnUpdate()) // Should we be triggering OnUpdate? Since we're fading out
                        {
                            yield break;
                        }
                    }
                }
            }
            #endregion

            EndPlaying();
        }

        private void UpdateSecondaryAudioPlayer(AudioPlayer secondaryAudioPlayer, bool setToEnd = false)
        {
            {
                var newPref = _pref; // shallow clone

                if (_pref.IsChainedMode())
                {
                    if (setToEnd)
                        newPref.ChainedModeStage = PlaybackStage.End;
                    else if (_pref.ChainedModeStage == PlaybackStage.Start)
                        newPref.ChainedModeStage = PlaybackStage.Loop;
                }

                if (newPref.IsLoop(LoopType.Loop))
                {
                    newPref.SetNextFadeIn(0f); // don't fade in on loop
                }

                secondaryAudioPlayer.SetPlaybackData(ID, newPref);
            }

            secondaryAudioPlayer.SetVolume(_trackVolume.Target);
            secondaryAudioPlayer.SetPitch(StaticPitch);
#if !UNITY_WEBGL
            secondaryAudioPlayer.SetTrackEffect(CurrentActiveTrackEffects, SetEffectMode.Override);
#endif
        }

        private void TransferToSecondaryAudioPlayer(ref AudioPlayer secondaryAudioPlayer)
        {
            _instanceWrapper.UpdateInstance(secondaryAudioPlayer);
            UpdateSecondaryAudioPlayer(secondaryAudioPlayer);

            if (_pref.ScheduledEndTime > 0.0)
            {
                secondaryAudioPlayer.SetScheduledEndTime(_pref.ScheduledEndTime);
            }

            ClearScheduleEndEvents(); // it should be rescheduled in the new player
            secondaryAudioPlayer = null;
            _instanceWrapper = null;
        }

        private void StartPlaying(int sampleRate)
        {
            switch (_stopMode)
            {
                case StopMode.Stop:
                    PlayFromPos(sampleRate);
                    break;
                case StopMode.Pause:
                    AudioSource.UnPause();
                    break;
                case StopMode.Mute:
                    if (!AudioSource.isPlaying)
                    {
                        PlayFromPos(sampleRate);
                    }
                    break;
            }
            _stopMode = default;
            _onStart?.Invoke(this);
            _onUpdate?.Invoke(this);
            _onStart = null;
        }

        private void PlayFromPos(int sampleRate)
        {
            SetPlayPosition(sampleRate);
            AudioSource.Play();
        }

        private void SetPlayPosition(int sampleRate)
        {
            AudioSource.Stop();
            AudioSource.timeSamples = GetSample(sampleRate, _clip.StartPosition);
        }

        #region Stop Overloads
        void IAudioStoppable.Pause()
            => this.Pause(FadeData.UseClipSetting);
        void IAudioStoppable.Pause(float fadeOut)
            => Stop(fadeOut, StopMode.Pause, null);
        void IAudioStoppable.UnPause()
            => this.UnPause(FadeData.UseClipSetting);
        void IAudioStoppable.UnPause(float fadeIn)
        {
            _pref.SetNextFadeIn(fadeIn);
            Play();
        }
        void IAudioStoppable.Stop()
            => this.Stop(FadeData.UseClipSetting);
        void IAudioStoppable.Stop(float fadeOut)
            => this.Stop(fadeOut, null);
        void IAudioStoppable.Stop(Action onFinished)
            => this.Stop(FadeData.UseClipSetting, onFinished);
        void IAudioStoppable.Stop(float fadeOut, Action onFinished)
            => Stop(fadeOut, StopMode.Stop, onFinished);
        #endregion
        public void Stop(float overrideFade, StopMode stopMode, Action onFinished)
        {
            if (IsStopping && !Mathf.Approximately(overrideFade, FadeData.Immediate))
            {
                return;
            }

            bool isPlaying = AudioSource.isPlaying;
            if(stopMode == StopMode.Pause && !isPlaying)
            {
                return;
            }

            if (!ID.IsValid() || !isPlaying)
            {
                onFinished?.Invoke();
                EndPlaying();
                return;
            }

            this.StartCoroutineAndReassign(StopControl(overrideFade, stopMode, onFinished), ref _playbackControlCoroutine);
        }

        private IEnumerator StopControl(float overrideFade, StopMode stopMode, Action onFinished)
        {
            _stopMode = stopMode;
            IsStopping = true;
            _pref.SetNextFadeOut(overrideFade);

            if (stopMode == StopMode.Stop && _pref.IsChainedMode() && _pref.ChainedModeStage != PlaybackStage.End)
            {
                if (queuedAudioPlayer != null) // early kill of any queued looping
                {
                    queuedAudioPlayer.EndPlaying();
                    queuedAudioPlayer = null;
                }

                // immediately handover to End stage

                var endPlayer = SoundManager.Instance.GetPooledAudioPlayer(ID, _instanceWrapper);
                UpdateSecondaryAudioPlayer(endPlayer, setToEnd: true);
                endPlayer.Play();
                TransferToSecondaryAudioPlayer(ref endPlayer);
            }

            if (_pref.HasFadeOut(_clip.FadeOut, out var fadeOut, out var fadeOutEase))
            {
                float elapsedTime = 0f;
                _clipVolume.SetTarget(0f);

                while (_clipVolume.Update(ref elapsedTime, fadeOut, fadeOutEase) && AudioSource.isPlaying)
                {
                    yield return null;
                }
            }

            switch (stopMode)
            {
                case StopMode.Stop:
                    EndPlaying();
                    break;
                case StopMode.Pause:
                    AudioSource.Pause();
                    break;
                case StopMode.Mute:
                    this.SetVolume(0f);
                    break;
            }

            IsStopping = false;
            onFinished?.Invoke();
        }

        private bool OnUpdate()
        {
            _onUpdate?.Invoke(this);
            return IsActive;
        }

        private void EndPlaying()
        {
            PlaybackStartingTime = 0;
            _stopMode = default;
            _pref = default;
            IsStopping = false;
            ResetVolume();
            ResetPitch();
            
            AudioSource.Stop();
            AudioSource.clip = null;
            _clip = null;
            ResetSpatial();
            ResetEffect();

            // Don't add StopCoroutine(_playbackCoroutine) here, as this method is typically called within it, and further processing after this method cannot be guaranteed.
            _trackVolume.StopCoroutine();
            _audioTypeVolume.StopCoroutine();

            _onEnd?.Invoke(ID);
            _onEnd = null;

            if (queuedAudioPlayer != null) // If we get to EndPlaying and we HAVEN'T handled the queued audio, it's never getting fired, so clean it up
            {
                queuedAudioPlayer.EndPlaying();
                queuedAudioPlayer = null;
            }

            Recycle();
        }
        
        private bool CanLoopIfIsChainedMode()
        {
            return !_pref.IsChainedMode() || (_pref.IsChainedMode() && _pref.ChainedModeStage == PlaybackStage.Loop);
        }

        public IAudioPlayer OnEnd(Action<SoundID> onEnd)
        {
            _onEnd -= onEnd;
            _onEnd += onEnd;
            return this;
        }

        public IAudioPlayer OnUpdate(Action<IAudioPlayer> onUpdate)
        {
            _onUpdate -= onUpdate;
            _onUpdate += onUpdate;
            return this;
        }

        public IAudioPlayer OnStart(Action<IAudioPlayer> onStart)
        {
            _onStart -= onStart;
            _onStart += onStart;
            return this;
        }

        public IAudioPlayer SetFadeInEase(Ease ease)
        {
            _pref.SetFadeInEase(ease);
            return this;
        }

        public IAudioPlayer SetFadeOutEase(Ease ease)
        {
            _pref.SetFadeOutEase(ease);
            return this;
        }
    }
}
