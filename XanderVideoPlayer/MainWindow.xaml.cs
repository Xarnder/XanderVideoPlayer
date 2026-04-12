using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;

namespace XanderVideoPlayer
{
    public class SubtitleItem
    {
        public long StartTimeMs { get; set; }
        public long EndTimeMs { get; set; }
        public string Text { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private LibVLC _libVLC = null!;
        private MediaPlayer _mediaPlayer = null!;
        private List<SubtitleItem> _subtitles = new List<SubtitleItem>();

        private bool _isDraggingSlider = false;
        private bool _isFullscreen = false;
        private string _moviePath = "";
        private string _tempSubPath = "temp_subs.srt";
        private double _cropValue = 0;
        private double _subAreaHeight = 120;

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();

            var initOptions = new string[]
            {
                "--vout=direct3d11",
                "--avcodec-hw=d3d11va",
                "--sub-track=-1",
                "--no-spu"
            };

            _libVLC = new LibVLC(initOptions);
            _mediaPlayer = new MediaPlayer(_libVLC);
            videoView.MediaPlayer = _mediaPlayer;

            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.Playing += MediaPlayer_Playing;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
        }

        // --- OPEN VIDEO LOGIC ---
        private void OpenFileBtn_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "Video Files (*.mp4;*.mkv)|*.mp4;*.mkv|All files (*.*)|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                _moviePath = openFileDialog.FileName;

                // Stop current video and clear its media so it's removed from the screen
                if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();
                _mediaPlayer.Media = null;

                // NEW: Better visual feedback!
                SubtitleText.Text = "⏳ Extracting Subtitles... Please wait (this may take a few seconds).";
                ExtractionProgress.Visibility = Visibility.Visible;

                TimelineSlider.Value = 0;
                SubTrackCombo.SelectionChanged -= SubTrackCombo_SelectionChanged;
                SubTrackCombo.SelectedIndex = 0;
                SubTrackCombo.SelectionChanged += SubTrackCombo_SelectionChanged;
                AudioCombo.Items.Clear();

                Task.Run(() =>
                {
                    ExtractSubtitles(_moviePath, _tempSubPath, 0);
                    Dispatcher.Invoke(() =>
                    {
                        LoadSubtitles(_tempSubPath);
                        using var media = new Media(_libVLC, _moviePath, FromType.FromPath);
                        _mediaPlayer.Play(media);
                        PlayPauseBtn.Content = "Pause";
                    });
                });
            }
        }

        // --- NEW: LOAD EXTERNAL .SRT LOGIC ---
        private void LoadSubBtn_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "Subtitle Files (*.srt)|*.srt|All files (*.*)|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                string externalSubPath = openFileDialog.FileName;
                SubtitleText.Text = "⏳ Loading external subtitles...";

                Task.Run(() =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        LoadSubtitles(externalSubPath);

                        // Clear the internal combo box selection so the user knows we are using external now
                        SubTrackCombo.SelectionChanged -= SubTrackCombo_SelectionChanged;
                        SubTrackCombo.SelectedIndex = -1;
                        SubTrackCombo.SelectionChanged += SubTrackCombo_SelectionChanged;

                        // Briefly show a success message
                        if (_subtitles.Count > 0)
                            SubtitleText.Text = "✅ External Subtitles Loaded Successfully.";
                    });
                });
            }
        }

        // --- AUDIO & SUBTITLE TRACK LOGIC ---
        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Populate Audio Tracks
                AudioCombo.Items.Clear();
                foreach (var track in _mediaPlayer.AudioTrackDescription)
                {
                    if (track.Id != -1)
                    {
                        ComboBoxItem item = new ComboBoxItem();
                        item.Content = track.Name;
                        item.Tag = track.Id;
                        AudioCombo.Items.Add(item);
                    }
                }

                if (AudioCombo.Items.Count > 0)
                {
                    AudioCombo.SelectedIndex = 0;
                    if (AudioCombo.SelectedItem is ComboBoxItem item && item.Tag is int trackId)
                    {
                        _mediaPlayer.SetAudioTrack(trackId);
                    }
                }

                // Populate Subtitle Tracks
                SubTrackCombo.SelectionChanged -= SubTrackCombo_SelectionChanged;
                SubTrackCombo.Items.Clear();
                int idx = 0;
                foreach (var track in _mediaPlayer.SpuDescription)
                {
                    if (track.Id != -1)
                    {
                        ComboBoxItem item = new ComboBoxItem();
                        item.Content = $"{track.Name} [#{++idx}]";
                        item.Tag = idx - 1; // Internal index for ffmpeg
                        SubTrackCombo.Items.Add(item);
                    }
                }
                if (SubTrackCombo.Items.Count > 0) SubTrackCombo.SelectedIndex = 0;
                SubTrackCombo.SelectionChanged += SubTrackCombo_SelectionChanged;

                // Ensure internal subtitles are disabled
                _mediaPlayer.SetSpu(-1);
                UpdateSubtitleStats();
            });
        }

        private void AudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AudioCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is int trackId)
            {
                _mediaPlayer.SetAudioTrack(trackId);
            }
        }

        // --- TIMELINE & CONTROLS ---
        private void SkipBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.Length > 0)
            {
                long newTime = _mediaPlayer.Time + 10000;
                _mediaPlayer.Time = Math.Min(newTime, _mediaPlayer.Length);
            }
        }

        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            Dispatcher.Invoke(() => TimelineSlider.Maximum = e.Length);
        }

        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isDraggingSlider = true;

        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e) => _isDraggingSlider = false;

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider || Math.Abs(e.NewValue - e.OldValue) > 1000)
                _mediaPlayer.Time = (long)e.NewValue;
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            long currentTime = e.Time;

            Dispatcher.Invoke(() =>
            {
                if (!_isDraggingSlider)
                {
                    TimelineSlider.ValueChanged -= TimelineSlider_ValueChanged;
                    TimelineSlider.Value = currentTime;
                    TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
                }

                var currentSub = _subtitles.Find(s => currentTime >= s.StartTimeMs && currentTime <= s.EndTimeMs);

                if (currentSub != null)
                {
                    SubtitleText.Text = currentSub.Text;
                }
                // NEW: Ensure we don't accidentally erase our loading messages while the video plays in the background!
                else if (!SubtitleText.Text.StartsWith("⏳") &&
                         !SubtitleText.Text.StartsWith("✅") &&
                         SubtitleText.Text != "No subtitles found." &&
                         SubtitleText.Text != "Error reading subtitle file." &&
                         SubtitleText.Text != "Open a video file to begin...")
                {
                    SubtitleText.Text = "";
                }
            });
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying) { _mediaPlayer.Pause(); PlayPauseBtn.Content = "Play"; }
            else { _mediaPlayer.Play(); PlayPauseBtn.Content = "Pause"; }
        }

        // --- SUBTITLE EXTRACTION LOGIC ---
        private void SubTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || string.IsNullOrEmpty(_moviePath) || SubTrackCombo.SelectedItem == null) return;

            int trackIndex = 0;
            string trackName = "Selected Track";
            
            if (SubTrackCombo.SelectedItem is ComboBoxItem item)
            {
                trackIndex = (int)item.Tag;
                trackName = item.Content.ToString() ?? "Selected Track";
            }

            SubtitleText.Text = $"⏳ Extracting {trackName}... Please wait.";
            ExtractionProgress.Visibility = Visibility.Visible;

            Task.Run(() =>
            {
                ExtractSubtitles(_moviePath, _tempSubPath, trackIndex);
                Dispatcher.Invoke(() => LoadSubtitles(_tempSubPath));
            });
        }

        private void ExtractSubtitles(string videoPath, string outputPath, int trackIndex)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg.exe",
                    Arguments = $"-y -i \"{videoPath}\" -map 0:s:{trackIndex} \"{outputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }

        private void LoadSubtitles(string srtPath)
        {
            ExtractionProgress.Visibility = Visibility.Collapsed;
            _subtitles.Clear();
            if (!File.Exists(srtPath) || new FileInfo(srtPath).Length == 0)
            {
                SubtitleText.Text = "No subtitles found.";
                return;
            }

            // NEW: A Try/Catch block so the app doesn't crash if you load a broken text file
            try
            {
                var text = File.ReadAllText(srtPath);
                var blocks = text.Split(new string[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var block in blocks)
                {
                    var lines = block.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
                    if (lines.Length >= 3)
                    {
                        var item = new SubtitleItem();
                        var timeParts = lines[1].Split(new[] { "-->" }, StringSplitOptions.None);
                        item.StartTimeMs = ParseTime(timeParts[0].Trim());
                        item.EndTimeMs = ParseTime(timeParts[1].Trim());
                        item.Text = string.Join("\n", lines, 2, lines.Length - 2);
                        _subtitles.Add(item);
                    }
                }

                // Clear the loading text once successfully parsed
                if (SubtitleText.Text.StartsWith("⏳ Extracting"))
                {
                    SubtitleText.Text = "";
                }
            }
            catch (Exception)
            {
                SubtitleText.Text = "Error reading subtitle file.";
            }
        }

        private long ParseTime(string timeStr)
        {
            timeStr = timeStr.Replace(',', '.');
            if (TimeSpan.TryParse(timeStr, out TimeSpan ts)) return (long)ts.TotalMilliseconds;
            return 0;
        }

        // --- FULLSCREEN LOGIC ---
        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F)
            {
                ToggleFullscreen();
            }
            else if (e.Key == System.Windows.Input.Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
            }
            else if (e.Key == System.Windows.Input.Key.Space)
            {
                PlayPauseBtn_Click(null!, null!);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Right)
            {
                long newTime = _mediaPlayer.Time + 10000;
                _mediaPlayer.Time = Math.Min(newTime, _mediaPlayer.Length);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Left)
            {
                long newTime = _mediaPlayer.Time - 10000;
                _mediaPlayer.Time = Math.Max(newTime, 0);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Up)
            {
                // Skip forward 1 minute
                long newTime = _mediaPlayer.Time + 60000;
                _mediaPlayer.Time = Math.Min(newTime, _mediaPlayer.Length);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Down)
            {
                // Skip backward 1 minute
                long newTime = _mediaPlayer.Time - 60000;
                _mediaPlayer.Time = Math.Max(newTime, 0);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.W)
            {
                SubtitleText.FontSize += 2;
                UpdateSubtitleStats();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.S)
            {
                SubtitleText.FontSize = Math.Max(12, SubtitleText.FontSize - 2);
                UpdateSubtitleStats();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.OemCloseBrackets || e.Key == System.Windows.Input.Key.Oem6)
            {
                SubtitleText.MaxWidth += 50;
                UpdateSubtitleStats();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.OemOpenBrackets)
            {
                SubtitleText.MaxWidth = Math.Max(200, SubtitleText.MaxWidth - 50);
                UpdateSubtitleStats();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.OemPlus || e.Key == System.Windows.Input.Key.Add)
            {
                ChangeCrop(5);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.OemMinus || e.Key == System.Windows.Input.Key.Subtract)
            {
                ChangeCrop(-5);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.M)
            {
                ChangeSubAreaHeight(10);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.N)
            {
                ChangeSubAreaHeight(-10);
                e.Handled = true;
            }
        }

        private void ChangeSubAreaHeight(double delta)
        {
            _subAreaHeight = Math.Max(0, _subAreaHeight + delta);
            UpdateSubtitleLayout();
        }

        private void UpdateSubtitleLayout()
        {
            double cropPixels = _cropValue * 5;
            if (SubtitleAreaBorder != null)
                SubtitleAreaBorder.MinHeight = _subAreaHeight + (cropPixels / 2);
            
            FlashPurpleLines();
            UpdateSubtitleStats();
        }

        private void CropIncBtn_Click(object sender, RoutedEventArgs e)
        {
            ChangeCrop(5);
        }

        private void CropDecBtn_Click(object sender, RoutedEventArgs e)
        {
            ChangeCrop(-5);
        }

        private void ChangeCrop(double delta)
        {
            _cropValue = Math.Max(0, _cropValue + delta);
            
            // Zoom the video
            double zoom = 1.0 + (_cropValue / 100.0);
            if (VideoScale != null)
            {
                VideoScale.ScaleX = zoom;
                VideoScale.ScaleY = zoom;
            }

            // Expose the custom black bar space (top)
            double cropPixels = _cropValue * 5; 
            
            if (TopCropRect != null)
                TopCropRect.Height = cropPixels;

            // Shift focus to the layout updater for the bottom bar and stats
            UpdateSubtitleLayout();
        }

        private int _cropFlashCount = 0;

        private async void FlashPurpleLines()
        {
            _cropFlashCount++;
            if (TopCropRect != null) TopCropRect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Purple);
            if (SubtitleAreaBorder != null) SubtitleAreaBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Purple);
            
            await Task.Delay(1500);
            
            _cropFlashCount--;
            if (_cropFlashCount == 0)
            {
                if (TopCropRect != null) TopCropRect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
                if (SubtitleAreaBorder != null) SubtitleAreaBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
            }
        }

        private void HelpBtn_Click(object sender, RoutedEventArgs e)
        {
            string helpText = "Keyboard Shortcuts:\n\n" +
                              "Space : Play / Pause\n" +
                              "F / Esc : Toggle Fullscreen\n" +
                              "Left / Right Arrows : Skip -/+ 10s\n" +
                              "Up / Down Arrows : Skip -/+ 1m\n" +
                              "W / S : Increase/Decrease Subtitle Font Size\n" +
                              "[ / ] : Decrease/Increase Subtitle Width\n" +
                              "- / = : Decrease/Increase Crop (Zoom/Remove Black Bars)\n" +
                              "M / N : Increase/Decrease Subtitle Area Bounds";
            System.Windows.MessageBox.Show(helpText, "Shortcuts Help", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            videoView.MediaPlayer = null;

            if (_isFullscreen)
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                ControlsGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                ControlsGrid.Visibility = Visibility.Visible;
            }

            videoView.MediaPlayer = _mediaPlayer;
        }

        private void SubPosSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SubTransform != null)
            {
                SubTransform.Y = e.NewValue;
                UpdateSubtitleStats();
            }
        }

        private void UpdateSubtitleStats()
        {
            if (SizeStatus == null || HeightStatus == null || WidthStatus == null || CropStatus == null) return;
            SizeStatus.Text = $"S:{(int)SubtitleText.FontSize}";
            HeightStatus.Text = $"H:{(int)SubPosSlider.Value}";
            WidthStatus.Text = $"W:{(int)SubtitleText.MaxWidth}";
            CropStatus.Text = $"C:{(int)_cropValue}";
        }
    }
}
