﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GalaSoft.MvvmLight.Messaging;
using Meridian.Controls;
using Meridian.Domain;
using Meridian.Extensions;
using Meridian.Model;
using Meridian.Resources.Localization;
using Meridian.Services.Media.Core;
using Meridian.View.Flyouts;
using Meridian.ViewModel.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Meridian.Services
{
    public static class AudioService
    {
        //private static MediaElement _mediaPlayerBase;
        private static MediaPlayerBase _mediaPlayer;
        private static IList<Audio> _originalPlaylist;
        private static ObservableCollection<Audio> _playlist;
        private static Audio _currentAudio;
        private static readonly DispatcherTimer _positionTimer;
        private static PlayerPlayState _state;
        private static int _playFailsCount;
        private static CancellationTokenSource _cancellationToken = new CancellationTokenSource();

        private static MediaPlayerBase MediaPlayer
        {
            get
            {
                if (_mediaPlayer == null)
                {

                    switch (Settings.Instance.MediaEngine)
                    {
                        case MediaEngine.Wmp:
                            _mediaPlayer = new WmpMediaPlayer();
                            break;
                        case MediaEngine.Uwp:
                            _mediaPlayer = new UwpMediaPlayer();
                            break;

                        case MediaEngine.NAudio:
                            _mediaPlayer = new NaudioMediaPlayer();
                            break;
                    }

                    _mediaPlayer.Initialize();
                    _mediaPlayer.MediaEnded += MediaPlayerOnMediaEnded;
                    _mediaPlayer.MediaFailed += MediaPlayerOnMediaFailed;
                    _mediaPlayer.MediaOpened += MediaPlayerOnMediaOpened;
                    _mediaPlayer.Volume = Volume;
                }

                return _mediaPlayer;
            }
        }

        private static PlayerPlayState State
        {
            get { return _state; }
            set
            {
                if (_state == value)
                    return;

                _state = value;

                if (_state == PlayerPlayState.Playing)
                    _positionTimer.Start();
                else
                    _positionTimer.Stop();

                _mediaPlayer.UpdateTransportControls(CurrentAudio);

                Messenger.Default.Send(new PlayStateChangedMessage() { NewState = value });
            }
        }

        public static Audio CurrentAudio
        {
            get
            {
                return _currentAudio;
            }
            set
            {
                var old = _currentAudio;
                _currentAudio = value;

                NotifyAudioChanged(old);
                _mediaPlayer.UpdateTransportControls(value);
            }
        }

        public static ObservableCollection<Audio> Playlist
        {
            get { return _playlist; }
            set
            {
                if (Shuffle)
                {
                    _originalPlaylist = value.ToList(); //save original playlist
                    _playlist = value;
                    _playlist.Shuffle();
                }
                else
                {
                    _originalPlaylist = value;
                    _playlist = value;
                }
            }
        }

        public static bool Shuffle
        {
            get { return Settings.Instance.Shuffle; }
            set
            {
                Settings.Instance.Shuffle = value;

                if (value)
                {
                    _playlist = new ObservableCollection<Audio>(_originalPlaylist.ToList()); //copy original playlist to current
                    _playlist.Shuffle(); //shuffle
                }
                else
                {
                    _playlist = new ObservableCollection<Audio>(_originalPlaylist);
                }
            }
        }

        public static bool Repeat
        {
            get { return Settings.Instance.Repeat; }
            set
            {
                Settings.Instance.Repeat = value;
            }
        }

        public static bool IsPlaying
        {
            get
            {
                return State == PlayerPlayState.Playing;
            }
        }

        public static TimeSpan CurrentAudioPosition
        {
            get
            {
                if (MediaPlayer == null)
                    return TimeSpan.Zero;

                return MediaPlayer.Position;
            }
            set
            {
                if (MediaPlayer == null)
                    return;

                if (MediaPlayer.Position.TotalSeconds == value.TotalSeconds)
                    return;

                MediaPlayer.Position = value;
            }
        }

        public static TimeSpan CurrentAudioDuration
        {
            get
            {
                if (MediaPlayer != null)
                    return MediaPlayer.Duration;

                return TimeSpan.Zero;
            }
        }

        public static float Volume
        {
            get { return Settings.Instance.Volume; }
            set
            {
                if (Settings.Instance.Volume == value)
                    return;

                Settings.Instance.Volume = value.Clamp(0f, 1f);
                MediaPlayer.Volume = Settings.Instance.Volume;
            }
        }

        static AudioService()
        {
            _positionTimer = new DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(500);
            _positionTimer.Tick += PositionTimerTick;
            if (IsPlaying)
                _positionTimer.Start();
        }

        public static void Play(Audio track)
        {
            CancelAsync();
            PlayInternal(track, _cancellationToken.Token);
        }

        private async static void PlayInternal(Audio track, CancellationToken token)
        {
            if (CurrentAudio != null)
            {
                CurrentAudio.IsPlaying = false;

                Stop();
            }

            track.IsPlaying = true;

            CurrentAudio = track;

            if (track.Source == null)
            {
                VkAudio vkAudio = null;
                try
                {
                    vkAudio = await DataService.GetAudioByArtistAndTitle(track.Artist, track.Title);
                }
                catch (Exception ex)
                {
                    LoggingService.Log(ex);
                }

                if (vkAudio != null)
                {
                    vkAudio.IsPlaying = true;
                    if (_playlist != null)
                    {
                        var playlistTrackIndex = _playlist.IndexOf(track);
                        if (playlistTrackIndex >= 0)
                        {
                            var playlistTrack = (VkAudio)_playlist[_playlist.IndexOf(track)];
                            playlistTrack.Id = vkAudio.Id;
                            playlistTrack.Source = vkAudio.Source;
                            playlistTrack.OwnerId = vkAudio.OwnerId;
                            playlistTrack.IsAddedByCurrentUser = vkAudio.IsAddedByCurrentUser;
                            playlistTrack.AlbumId = vkAudio.AlbumId;
                            playlistTrack.Title = vkAudio.Title;
                            playlistTrack.Artist = vkAudio.Artist;
                            playlistTrack.Duration = vkAudio.Duration;
                            playlistTrack.GenreId = vkAudio.GenreId;
                            playlistTrack.LyricsId = vkAudio.LyricsId;
                            //_playlist[_playlist.IndexOf(track)] = vkAudio; //to fix radio vk scrobbling
                        }
                    }

                    track = vkAudio;
                    //_currentAudio = track;
                    _playFailsCount = 0;
                }
                else
                {
                    LoggingService.Log("Failed to find audio " + track.Artist + " - " + track.Title);

                    _playFailsCount++;
                    if (_playFailsCount > 5)
                        return;
                    
                    Next();

                    return;
                }
            }

#if DEBUG
            LoggingService.Log(string.Format("Playing: {0} {1} {2} {3}", track.Id, track.Artist, track.Title, track.Source));
#endif

            if (token.IsCancellationRequested)
                return;

            track.IsPlaying = true;

            //look like MediaElement doen't work with https, temporary hack
            var url = track.Source;
            if (!string.IsNullOrEmpty(url))
            {
                if (!Settings.Instance.UseHttps)
                    url = url.Replace("https://", "http://");

                MediaPlayer.Source = new Uri(url);
                MediaPlayer.Play();

                State = PlayerPlayState.Playing;
            }
        }

        public static void Play()
        {
            if (MediaPlayer.Source == null && CurrentAudio != null)
            {
                Play(CurrentAudio);
                //MediaPlayer.Source = new Uri(CurrentAudio.Source);
                //CurrentAudio.IsPlaying = true;
            }

            MediaPlayer.Play();

            State = PlayerPlayState.Playing;

            //MediaPlayerBase.Position = CurrentAudioPosition;
        }

        public static void PlayNext(Audio audio)
        {
            if (Playlist != null && CurrentAudio != null)
            {
                var currentAudio = Playlist.FirstOrDefault(a => a.Id == CurrentAudio.Id);
                if (currentAudio == null)
                    return;

                var index = Playlist.IndexOf(currentAudio);
                if (index >= 0)
                {
                    index++;
                    var newAudio = audio.Clone();
                    Playlist.Insert(index, newAudio);
                }
            }
        }

        public static void Pause()
        {
            MediaPlayer.Pause();

            State = PlayerPlayState.Paused;
        }

        public static void Stop()
        {
            MediaPlayer.Stop();

            State = PlayerPlayState.Stopped;
        }

        public static void FastForward(int step)
        {
            if (CurrentAudioPosition.TotalSeconds + step < CurrentAudio.Duration.TotalSeconds)
                CurrentAudioPosition += TimeSpan.FromSeconds(step);
            else
                SwitchNext();
        }

        public static void Rewind(int step)
        {
            if (CurrentAudioPosition.TotalSeconds - step > 0)
                CurrentAudioPosition -= TimeSpan.FromSeconds(step);
            else
                CurrentAudioPosition = TimeSpan.Zero;
        }

        /// <summary>
        /// Перейти к следующему треку. Обычно вызывается при нажатии пользователем кнопки Next.
        /// </summary>
        public static void SkipNext()
        {
            //если прошло больше 1/3 трека, считаем, что трек послушали полностью
            if (CurrentAudioPosition.TotalSeconds > CurrentAudioDuration.TotalSeconds / 3)
                SwitchNext();
            else
                Next(true);
        }

        /// <summary>
        /// Переключиться на следующий трек. Обычно вызывается автоматически при окончании текущего трека.
        /// </summary>
        public static void SwitchNext()
        {
            Next();
        }

        private static void Next(bool invokedByUser = false)
        {
            if (Repeat && !invokedByUser)
            {
                //
                Play(CurrentAudio);
                NotifyAudioChanged(CurrentAudio); //to scrobble repeating track
                return;
            }


            if (_playlist != null && _playlist.Count > 0)
            {
                int currentIndex = -1;
                if (_currentAudio != null)
                {
                    currentIndex = _playlist.IndexOf(_currentAudio);
                    if (currentIndex == -1)
                    {
                        var current = _playlist.FirstOrDefault(a => a.Id == _currentAudio.Id);
                        if (current != null)
                            currentIndex = _playlist.IndexOf(current);
                    }
                }

                currentIndex++;

                if (currentIndex >= _playlist.Count)
                {
                    currentIndex = 0;
                }

                Play(_playlist[currentIndex]);
            }
        }

        public static void Prev()
        {
            if (CurrentAudioPosition.TotalSeconds > 3)
            {
                CurrentAudioPosition = TimeSpan.Zero;
                return;
            }

            if (_playlist != null)
            {
                int currentIndex = -1;
                if (_currentAudio != null)
                {
                    var current = _playlist.FirstOrDefault(a => a.Id == _currentAudio.Id);
                    if (current != null)
                        currentIndex = _playlist.IndexOf(current);
                }

                currentIndex--;

                if (currentIndex >= 0)
                    Play(_playlist[currentIndex]);
            }
        }

        public static void SetCurrentPlaylist(IEnumerable<Audio> playlist, bool radio = false)
        {
            if (playlist == null)
            {
                Playlist?.Clear();
            }
            else
                Playlist = new ObservableCollection<Audio>(playlist);
        }

        public static Task Load()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists("currentPlaylist.js"))
                        return;

                    var json = File.ReadAllText("currentPlaylist.js");
                    if (string.IsNullOrEmpty(json))
                        return;

                    var o = JObject.Parse(json);
                    if (o["currentAudio"] != null)
                    {
                        var audio = JsonConvert.DeserializeObject<Audio>(o["currentAudio"].ToString(), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Objects });
                        Application.Current.Dispatcher.Invoke(() => 
                        { 
                            CurrentAudio = audio;
                            _mediaPlayer.UpdateTransportControls(CurrentAudio);
                        });
                    }

                    if (o["currentPlaylist"] != null)
                    {
                        var playlist = JsonConvert.DeserializeObject<List<object>>(o["currentPlaylist"].ToString(), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Objects });
                        if (playlist != null)
                            Application.Current.Dispatcher.Invoke(() => SetCurrentPlaylist(playlist.OfType<Audio>()));
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Log(ex);
                }
            });
        }

        public static void Save()
        {
            try
            {
                var o = new
                {
                    currentAudio = CurrentAudio,
                    currentPlaylist = Playlist
                };

                var json = JsonConvert.SerializeObject(o, new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Objects });
                File.WriteAllText("currentPlaylist.js", json);
            }
            catch (Exception ex)
            {
                LoggingService.Log(ex);
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists("currentPlaylist.js"))
                    File.Delete("currentPlaylist.js");
            }
            catch (Exception ex)
            {
                LoggingService.Log(ex);
            }
        }

        public static void Dispose()
        {
            _positionTimer.Stop();
            _mediaPlayer.Dispose();
        }

        private static void NotifyAudioChanged(Audio oldAudio = null)
        {
            Messenger.Default.Send(new CurrentAudioChangedMessage
            {
                OldAudio = oldAudio,
                NewAudio = CurrentAudio
            });
        }

        private static void PositionTimerTick(object sender, object e)
        {
            try
            {
                Messenger.Default.Send(new PlayerPositionChangedMessage() { NewPosition = MediaPlayer.Position });

                //possible fix for not switching tracks issue
                if (MediaPlayer.Position > CurrentAudio.Duration)
                    MediaPlayerOnMediaEnded(MediaPlayer, EventArgs.Empty);
            }
            catch (Exception ex)
            {

                LoggingService.Log(ex);
            }
        }

        private static void CancelAsync()
        {
            _cancellationToken.Cancel();
            _cancellationToken = new CancellationTokenSource();
        }

        private static void MediaPlayerOnMediaOpened(object sender, EventArgs e)
        {
            _playFailsCount = 0;
            State = PlayerPlayState.Playing;
        }

        private static void MediaPlayerOnMediaFailed(object sender, Exception e)
        {
            if (CurrentAudio != null)
                LoggingService.Log("Media failed " + CurrentAudio.Id + " " + CurrentAudio.Artist + " - " + CurrentAudio.Title + ". " + e);

            if (e is InvalidWmpVersionException)
            {
                var flyout = new FlyoutControl();
                flyout.FlyoutContent = new CommonMessageView() { Header = ErrorResources.AudioFailedErrorHeaderCommon, Message = ErrorResources.WmpMissingError };
                flyout.Show();
                return;
            }

            if (e is COMException)
            {
                var com = (COMException)e;
                if ((uint)com.ErrorCode == 0xC00D0035) //not found or connection problem
                {
                    var flyout = new FlyoutControl();
                    flyout.FlyoutContent = new CommonMessageView() { Header = ErrorResources.AudioFailedErrorHeaderCommon, Message = ErrorResources.WmpMissingError };
                    flyout.Show();

                    return;
                }
            }

            _playFailsCount++;
            if (_playFailsCount < 5)
            {
                if (e is FileNotFoundException && CurrentAudio is VkAudio)
                {
                    CurrentAudio.Source = null;
                    PlayInternal(CurrentAudio, _cancellationToken.Token);
                }
                else
                {
                    Next();
                }
            }
        }

        private static void MediaPlayerOnMediaEnded(object sender, EventArgs e)
        {
            SwitchNext();
        }
    }
}
