#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FModel.BlueprintGraph.Animation;
using FModel.Settings;

namespace FModel.Views.BlueprintViewer;

// The animation preview of the designer tab: a clock (AnimationPlayer) driven by a timer, whose state the canvas draws.
// Read only: nothing here changes the asset.
public partial class BlueprintViewerWindow
{
    private readonly AnimationPlayer _player = new();
    private DispatcherTimer? _animationTimer;
    private DateTime _lastAnimationTick;
    private bool _syncingAnimationUi;
    private bool _syncingAnimationSpeed;

    private void OnAnimationSelected(object sender, SelectionChangedEventArgs e)
    {
        StopAnimationTimer();
        _player.SetAnimation(AnimationCombo.SelectedItem as WidgetAnim);
        _player.Loop = UserSettings.Default.AnimationPreviewLoop;
        AnimationLoopCheck.IsChecked = _player.Loop;
        UpdateGameSpeedItem();
        AnimationCombo.ToolTip = _player.Animation == null ? null : DescribeAnimation(_player.Animation);

        _syncingAnimationUi = true;
        AnimationPlayButton.IsChecked = false;
        AnimationPlayButton.Content = "Play";
        AnimationSlider.Minimum = _player.Animation?.StartSeconds ?? 0;
        AnimationSlider.Maximum = Math.Max(_player.Animation?.EndSeconds ?? 1, AnimationSlider.Minimum + 0.001);
        AnimationSlider.Value = AnimationSlider.Minimum;
        _syncingAnimationUi = false;

        ApplyAnimation();
        RefreshAnimationEditor();
    }

    /// <summary>Refreshes the visible preview after an agent edit or after discarding pending animation edits.</summary>
    private void OnDesignerAnimationsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnDesignerAnimationsChanged));
            return;
        }

        var animation = AnimationCombo.SelectedItem as WidgetAnim;
        if (!ReferenceEquals(_player.Animation, animation))
        {
            var wasPlaying = _player.IsPlaying;
            var time = _player.Time;
            _player.SetAnimation(animation);
            if (animation != null)
            {
                _player.Time = Math.Clamp(time, animation.StartSeconds, animation.EndSeconds);
                if (wasPlaying)
                    _player.Play();
            }
        }

        UpdateGameSpeedItem();
        AnimationCombo.ToolTip = _player.Animation == null ? null : DescribeAnimation(_player.Animation);
        _syncingAnimationUi = true;
        AnimationSlider.Minimum = _player.Animation?.StartSeconds ?? 0;
        AnimationSlider.Maximum = Math.Max(_player.Animation?.EndSeconds ?? 1, AnimationSlider.Minimum + 0.001);
        if (_player.Animation != null)
            _player.Time = Math.Clamp(_player.Time, _player.Animation.StartSeconds, _player.Animation.EndSeconds);
        AnimationSlider.Value = Math.Clamp(_player.Time, AnimationSlider.Minimum, AnimationSlider.Maximum);
        _syncingAnimationUi = false;
        ApplyAnimation();
        RefreshAnimationEditor();
    }

    private void OnAnimationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAnimationSettingsChanged(sender, e)));
            return;
        }

        if (e.PropertyName == nameof(UserSettings.AnimationPreviewLoop))
        {
            _player.Loop = UserSettings.Default.AnimationPreviewLoop;
            AnimationLoopCheck.IsChecked = _player.Loop;
        }
        else if (e.PropertyName is nameof(UserSettings.AnimationPreviewSpeed) or nameof(UserSettings.AnimationPreviewUseBlueprintSpeed))
        {
            if (!_syncingAnimationSpeed)
                UpdateGameSpeedItem();
        }
    }

    private ComboBoxItem? _gameSpeedItem;

    /// <summary>
    /// Offers the speed the blueprint's own PlayAnimation call uses ("Game (8x)") and selects it: on screen an animation lasts its
    /// timeline length divided by that speed, which is often 2x to 8x for short UI animations.
    /// </summary>
    private void UpdateGameSpeedItem()
    {
        _syncingAnimationSpeed = true;
        try
        {
            if (_gameSpeedItem != null)
                AnimationSpeedCombo.Items.Remove(_gameSpeedItem);
            _gameSpeedItem = null;

            if (_player.Animation?.GameSpeed is { } speed)
            {
                _gameSpeedItem = new ComboBoxItem
                {
                    Content = $"Game ({speed:0.##}x)",
                    Tag = speed.ToString(CultureInfo.InvariantCulture),
                    ToolTip = "The playback speed the blueprint passes to PlayAnimation",
                };
                AnimationSpeedCombo.Items.Insert(0, _gameSpeedItem);
            }

            var useBlueprintSpeed = UserSettings.Default.AnimationPreviewUseBlueprintSpeed;
            var selected = useBlueprintSpeed ? _gameSpeedItem : null;
            if (selected == null)
            {
                var defaultTag = UserSettings.Default.AnimationPreviewSpeed.ToString(CultureInfo.InvariantCulture);
                selected = AnimationSpeedCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(o => o.Tag as string == defaultTag)
                           ?? AnimationSpeedCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(o => o.Tag as string == "1");
            }

            AnimationSpeedCombo.SelectedItem = selected;
            if (selected is ComboBoxItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var selectedSpeed))
                _player.Speed = selectedSpeed;
        }
        finally
        {
            _syncingAnimationSpeed = false;
        }
    }

    private static string DescribeAnimation(WidgetAnim animation)
    {
        var lines = new List<string>
        {
            $"{animation.Name}: timeline {animation.LengthSeconds:0.###} s, something moves for {animation.ActiveSeconds:0.###} s",
        };
        if (animation.PlayCalls.Count == 0)
        {
            lines.Add("No constant PlayAnimation call found in this blueprint's code.");
        }
        else
        {
            lines.Add("The blueprint plays it with:");
            lines.AddRange(animation.PlayCalls.Select(o => $"  {o}  ({o.Function})"));
            if (animation.GameSpeed is { } speed)
                lines.Add($"On screen the forward play lasts about {animation.LengthSeconds / speed * 1000:0.#} ms.");
        }
        return string.Join("\n", lines);
    }

    private void OnAnimationPlayClick(object sender, RoutedEventArgs e)
    {
        if (_player.Animation == null)
        {
            AnimationPlayButton.IsChecked = false;
            return;
        }

        if (AnimationPlayButton.IsChecked == true)
        {
            _player.Play();
            AnimationPlayButton.Content = "Pause";
            StartAnimationTimer();
        }
        else
        {
            _player.Pause();
            AnimationPlayButton.Content = "Play";
            StopAnimationTimer();
        }
    }

    private void OnAnimationStopClick(object sender, RoutedEventArgs e)
    {
        StopAnimationTimer();
        _player.Stop();
        _syncingAnimationUi = true;
        AnimationPlayButton.IsChecked = false;
        AnimationPlayButton.Content = "Play";
        AnimationSlider.Value = _player.Time;
        _syncingAnimationUi = false;
        ApplyAnimation();
    }

    private void OnAnimationLoopClick(object sender, RoutedEventArgs e)
    {
        _player.Loop = AnimationLoopCheck.IsChecked == true;
        UserSettings.Default.AnimationPreviewLoop = _player.Loop;
        UserSettings.Save();
    }

    private void OnAnimationSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAnimationSpeed)
            return;

        if (AnimationSpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            _player.Speed = speed;
            if (ReferenceEquals(AnimationSpeedCombo.SelectedItem, _gameSpeedItem))
            {
                UserSettings.Default.AnimationPreviewUseBlueprintSpeed = true;
            }
            else
            {
                _syncingAnimationSpeed = true;
                try
                {
                    UserSettings.Default.AnimationPreviewUseBlueprintSpeed = false;
                    UserSettings.Default.AnimationPreviewSpeed = speed;
                }
                finally
                {
                    _syncingAnimationSpeed = false;
                }
            }
            UserSettings.Save();
        }
    }

    private void OnAnimationSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingAnimationUi || _player.Animation == null)
            return;

        // scrubbing pauses playback
        if (_player.IsPlaying)
        {
            _player.Pause();
            StopAnimationTimer();
            AnimationPlayButton.IsChecked = false;
            AnimationPlayButton.Content = "Play";
        }
        _player.Time = e.NewValue;
        ApplyAnimation();
    }

    private void StartAnimationTimer()
    {
        _lastAnimationTick = DateTime.UtcNow;
        if (_animationTimer == null)
        {
            _animationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _animationTimer.Tick += OnAnimationTick;
            Unloaded += (_, _) => StopAnimationTimer();
        }
        _animationTimer.Start();
    }

    private void StopAnimationTimer() => _animationTimer?.Stop();

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastAnimationTick).TotalSeconds;
        _lastAnimationTick = now;

        if (_player.Advance(elapsed))
        {
            _syncingAnimationUi = true;
            AnimationSlider.Value = Math.Clamp(_player.Time, AnimationSlider.Minimum, AnimationSlider.Maximum);
            _syncingAnimationUi = false;
            ApplyAnimation();
        }

        if (!_player.IsPlaying)
        {
            StopAnimationTimer();
            AnimationPlayButton.IsChecked = false;
            AnimationPlayButton.Content = "Play";
        }
    }

    /// <summary>Evaluates the selected animation at the current time and hands the result to the canvas.</summary>
    private void ApplyAnimation()
    {
        var root = _viewModel.Designer.Info?.Root;
        DesignerCanvas.SetAnimation(root == null ? null : _player.State(root));
        AnimationTimeText.Text = _player.Animation == null ? "" : $"{_player.Time:0.000} / {_player.Animation.EndSeconds:0.000} s";
    }
}
