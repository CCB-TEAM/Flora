#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FModel.BlueprintGraph.Animation;
using FModel.Framework.BlueprintAgent;
using FModel.Settings;
using Newtonsoft.Json.Linq;

namespace FModel.Views.BlueprintViewer;

public partial class BlueprintViewerWindow
{
    private const double AnimationTimelineLabelWidth = 236;
    private const double AnimationTimelineRightPadding = 18;
    private const double AnimationTimelineRulerHeight = 28;
    private const double AnimationTimelineRowHeight = 25;
    private static readonly CultureInfo AnimationEditorCulture = CultureInfo.InvariantCulture;

    private readonly List<AnimationEditorRow> _animationEditorRows = [];
    private WidgetAnim? _animationEditorAnimation;
    private AnimationEditorRow? _selectedAnimationEditorRow;
    private int _selectedAnimationEditorKey = -1;
    private double _animationEditorDraftTime;
    private double _timelineStartSeconds;
    private double _timelineEndSeconds = 1;
    private double _timelineXStart = AnimationTimelineLabelWidth;
    private double _timelineXWidth = 500;
    private bool _animationTimelineDragging;
    private Ellipse? _animationTimelineDragMarker;

    private sealed record AnimationEditorRow(string Widget, string TrackClass, string Property, int Section, AnimChannel Channel)
    {
        public string Identity => $"{Widget}\0{TrackClass}\0{Property}\0{Section}\0{Channel.Name}";
        public string Label => $"{Widget} / {Property}.{Channel.Name} / section[{Section}]";
    }

    private sealed record AnimationEditorMarker(AnimationEditorRow Row, int KeyIndex);

    private void RefreshAnimationEditor()
    {
        if (AnimationTimelineCanvas == null)
            return;

        var animation = AnimationCombo.SelectedItem as WidgetAnim;
        if (!ReferenceEquals(animation, _animationEditorAnimation))
        {
            _animationEditorAnimation = animation;
            _selectedAnimationEditorRow = null;
            _selectedAnimationEditorKey = -1;
        }

        _animationEditorRows.Clear();
        if (animation != null)
        {
            foreach (var binding in animation.Bindings)
            foreach (var track in binding.Tracks)
            foreach (var section in track.Sections)
            foreach (var channel in section.Channels.Where(o => o.Keys.Length > 0))
                _animationEditorRows.Add(new AnimationEditorRow(binding.Widget, track.ClassName, track.Property, section.Index, channel));
        }

        if (_selectedAnimationEditorRow != null)
        {
            var previousIdentity = _selectedAnimationEditorRow.Identity;
            _selectedAnimationEditorRow = _animationEditorRows.FirstOrDefault(o => o.Identity == previousIdentity &&
                _selectedAnimationEditorKey >= 0 && _selectedAnimationEditorKey < o.Channel.Keys.Length);
            if (_selectedAnimationEditorRow == null)
                _selectedAnimationEditorKey = -1;
        }

        var keyCount = _animationEditorRows.Sum(o => o.Channel.Keys.Length);
        AnimationEditorStatus.Text = animation == null
            ? "Select an animation above."
            : $"{_animationEditorRows.Count} channels · {keyCount} keys. Apply stages changes in this blueprint.";
        AnimationRangeStart.IsEnabled = animation != null;
        AnimationRangeEnd.IsEnabled = animation != null;
        AnimationRangeStart.Text = animation?.StartSeconds.ToString("0.####", AnimationEditorCulture) ?? "";
        AnimationRangeEnd.Text = animation?.EndSeconds.ToString("0.####", AnimationEditorCulture) ?? "";

        RefreshSelectedAnimationKeyFields();
        RenderAnimationTimeline();
    }

    private void RefreshSelectedAnimationKeyFields()
    {
        var row = _selectedAnimationEditorRow;
        var index = _selectedAnimationEditorKey;
        var hasKey = row != null && index >= 0 && index < row.Channel.Keys.Length;
        AnimationSelectedKeyText.Text = hasKey ? $"{row!.Label} · key {index}" : "Select a key marker";
        AnimationSelectedKeyText.ToolTip = hasKey ? $"{row!.Label} · key {index} (zero-based)" : null;
        AnimationKeyTime.IsEnabled = hasKey;
        AnimationKeyValue.IsEnabled = hasKey;
        AnimationKeyInterp.IsEnabled = hasKey;
        AnimationKeyTangentMode.IsEnabled = hasKey;
        AnimationKeyArriveTangent.IsEnabled = hasKey;
        AnimationKeyLeaveTangent.IsEnabled = hasKey;
        AnimationApplyKeyEnabled(hasKey);
        if (!hasKey)
        {
            AnimationKeyTime.Text = "";
            AnimationKeyValue.Text = "";
            AnimationKeyArriveTangent.Text = "";
            AnimationKeyLeaveTangent.Text = "";
            AnimationKeyInterp.SelectedIndex = -1;
            AnimationKeyTangentMode.SelectedIndex = -1;
            return;
        }

        var animation = _animationEditorAnimation!;
        var channel = row!.Channel;
        var key = channel.Keys[index];
        _animationEditorDraftTime = channel.Times[index] / channel.TicksPerSecond;
        AnimationKeyTime.Text = _animationEditorDraftTime.ToString("0.####", AnimationEditorCulture);
        AnimationKeyValue.Text = key.Value.ToString("0.####", AnimationEditorCulture);
        AnimationKeyArriveTangent.Text = (key.ArriveTangent * channel.TicksPerSecond).ToString("0.####", AnimationEditorCulture);
        AnimationKeyLeaveTangent.Text = (key.LeaveTangent * channel.TicksPerSecond).ToString("0.####", AnimationEditorCulture);
        SelectEnumItem(AnimationKeyInterp, key.Interp);
        SelectEnumItem(AnimationKeyTangentMode, key.TangentMode);
        UpdateAnimationTangentInputState();
    }

    private void AnimationApplyKeyEnabled(bool hasKey)
    {
        if (AnimationKeyApplyButton != null)
            AnimationKeyApplyButton.IsEnabled = hasKey;
    }

    private static void SelectEnumItem(ComboBox combo, Enum value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(o =>
            string.Equals(o.Tag as string, value.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadSelectedEnum<TEnum>(ComboBox combo, out TEnum value) where TEnum : struct, Enum
    {
        var token = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse(token, true, out value) && Enum.IsDefined(value);
    }

    private void RenderAnimationTimeline()
    {
        var canvas = AnimationTimelineCanvas;
        var animation = _animationEditorAnimation;
        if (canvas == null)
            return;

        canvas.Children.Clear();
        var viewportWidth = AnimationTimelineScrollViewer.ViewportWidth;
        var width = Math.Max(760, double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 900);
        var height = Math.Max(82, AnimationTimelineRulerHeight + _animationEditorRows.Count * AnimationTimelineRowHeight + 8);
        canvas.Width = width;
        canvas.Height = height;
        _timelineXStart = AnimationTimelineLabelWidth;
        _timelineXWidth = Math.Max(180, width - _timelineXStart - AnimationTimelineRightPadding);

        if (animation == null)
        {
            AddTimelineText("Choose an animation in the preview toolbar.", 10, 18, 12, Brushes.LightGray);
            return;
        }

        var keyTimes = _animationEditorRows.SelectMany(o => o.Channel.Times.Select(t => t / o.Channel.TicksPerSecond)).ToList();
        _timelineStartSeconds = Math.Min(animation.StartSeconds, keyTimes.Count == 0 ? animation.StartSeconds : keyTimes.Min());
        _timelineEndSeconds = Math.Max(animation.EndSeconds, keyTimes.Count == 0 ? animation.EndSeconds : keyTimes.Max());
        if (_timelineEndSeconds - _timelineStartSeconds < 0.001)
            _timelineEndSeconds = _timelineStartSeconds + 1;

        AddTimelineRectangle(0, 0, width, AnimationTimelineRulerHeight, "#292D37");
        AddTimelineLine(_timelineXStart, 0, _timelineXStart, height, "#4B5260", 1);
        var tick = NiceTimelineTick(_timelineEndSeconds - _timelineStartSeconds, _timelineXWidth);
        var firstTick = Math.Ceiling(_timelineStartSeconds / tick) * tick;
        for (var seconds = firstTick; seconds <= _timelineEndSeconds + tick * 0.0001 && seconds < firstTick + tick * 250; seconds += tick)
        {
            var x = TimelineX(seconds);
            AddTimelineLine(x, 0, x, height, "#343A46", 1);
            AddTimelineText(seconds.ToString("0.###", AnimationEditorCulture), x + 3, 5, 10, Brushes.Gainsboro);
        }

        AddTimelineText($"{animation.Name} · {animation.DisplayRate:0.##} fps · {animation.TicksPerSecond:0} ticks/s", 8, 7, 10, Brushes.LightGray);
        for (var rowIndex = 0; rowIndex < _animationEditorRows.Count; rowIndex++)
        {
            var row = _animationEditorRows[rowIndex];
            var y = AnimationTimelineRulerHeight + rowIndex * AnimationTimelineRowHeight;
            AddTimelineRectangle(0, y, width, AnimationTimelineRowHeight, rowIndex % 2 == 0 ? "#242832" : "#20232B");
            AddTimelineLine(0, y + AnimationTimelineRowHeight, width, y + AnimationTimelineRowHeight, "#343A46", 1);
            AddTimelineText(row.Label, 8, y + 5, 11, Brushes.Gainsboro, AnimationTimelineLabelWidth - 18);
            for (var keyIndex = 0; keyIndex < row.Channel.Keys.Length; keyIndex++)
            {
                var time = row.Channel.Times[keyIndex] / row.Channel.TicksPerSecond;
                var x = TimelineX(time);
                var isSelected = IsSelected(row, keyIndex);
                var marker = new Ellipse
                {
                    Width = isSelected ? 13 : 10,
                    Height = isSelected ? 13 : 10,
                    Fill = isSelected ? Brushes.Orange : Brushes.DeepSkyBlue,
                    Stroke = Brushes.White,
                    StrokeThickness = isSelected ? 1.5 : 0.8,
                    Cursor = Cursors.SizeWE,
                    Tag = new AnimationEditorMarker(row, keyIndex),
                    ToolTip = $"{row.Label}\nKey {keyIndex}: {row.Channel.Keys[keyIndex].Value:0.####} at {time:0.####} s\nDrag to move; Apply key to stage."
                };
                marker.MouseLeftButtonDown += OnAnimationTimelineKeyMouseDown;
                marker.MouseMove += OnAnimationTimelineKeyMouseMove;
                marker.MouseLeftButtonUp += OnAnimationTimelineKeyMouseUp;
                Canvas.SetLeft(marker, x - marker.Width / 2);
                Canvas.SetTop(marker, y + (AnimationTimelineRowHeight - marker.Height) / 2);
                canvas.Children.Add(marker);
            }
        }
    }

    private bool IsSelected(AnimationEditorRow row, int keyIndex) => _selectedAnimationEditorRow != null &&
        _selectedAnimationEditorRow.Identity == row.Identity && _selectedAnimationEditorKey == keyIndex;

    private double TimelineX(double seconds) => _timelineXStart + (seconds - _timelineStartSeconds) /
        (_timelineEndSeconds - _timelineStartSeconds) * _timelineXWidth;

    private static double NiceTimelineTick(double span, double width)
    {
        var raw = span / Math.Max(1, width / 90);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 0.000001))));
        var normalized = raw / magnitude;
        var factor = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return factor * magnitude;
    }

    private void AddTimelineText(string text, double left, double top, double fontSize, Brush foreground, double maxWidth = double.PositiveInfinity)
    {
        var label = new TextBlock { Text = text, FontSize = fontSize, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = maxWidth };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        AnimationTimelineCanvas.Children.Add(label);
    }

    private void AddTimelineLine(double x1, double y1, double x2, double y2, string color, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush((Color) ColorConverter.ConvertFromString(color)), StrokeThickness = thickness, IsHitTestVisible = false };
        AnimationTimelineCanvas.Children.Add(line);
    }

    private void AddTimelineRectangle(double left, double top, double width, double height, string color)
    {
        var rectangle = new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = new SolidColorBrush((Color) ColorConverter.ConvertFromString(color)), IsHitTestVisible = false };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        AnimationTimelineCanvas.Children.Add(rectangle);
    }

    private void OnAnimationTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RenderAnimationTimeline();

    private void OnAnimationTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_animationEditorAnimation == null || e.OriginalSource is not Canvas || _timelineXWidth <= 0)
            return;
        ScrubAnimation(TimelineSeconds(e.GetPosition(AnimationTimelineCanvas).X));
    }

    private void OnAnimationTimelineKeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse marker || marker.Tag is not AnimationEditorMarker target)
            return;
        _selectedAnimationEditorRow = target.Row;
        _selectedAnimationEditorKey = target.KeyIndex;
        RefreshSelectedAnimationKeyFields();
        UpdateSelectedAnimationMarkerAppearance();
        _animationTimelineDragging = true;
        _animationTimelineDragMarker = marker;
        marker.CaptureMouse();
        e.Handled = true;
    }

    private void OnAnimationTimelineKeyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_animationTimelineDragging || sender is not Ellipse marker || marker.Tag is not AnimationEditorMarker target ||
            !ReferenceEquals(marker, _animationTimelineDragMarker))
            return;
        var time = ClampDraftKeyTime(target.Row.Channel, target.KeyIndex, TimelineSeconds(e.GetPosition(AnimationTimelineCanvas).X));
        _animationEditorDraftTime = time;
        AnimationKeyTime.Text = time.ToString("0.####", AnimationEditorCulture);
        Canvas.SetLeft(marker, TimelineX(time) - marker.Width / 2);
        AnimationEditorStatus.Text = $"Draft move: key {target.KeyIndex} → {time:0.####} s. Click Apply key to stage.";
        e.Handled = true;
    }

    private void OnAnimationTimelineKeyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse marker && ReferenceEquals(marker, _animationTimelineDragMarker))
        {
            marker.ReleaseMouseCapture();
            _animationTimelineDragging = false;
            _animationTimelineDragMarker = null;
            e.Handled = true;
        }
    }

    private void UpdateSelectedAnimationMarkerAppearance()
    {
        foreach (var marker in AnimationTimelineCanvas.Children.OfType<Ellipse>())
        {
            if (marker.Tag is not AnimationEditorMarker key)
                continue;
            var selected = IsSelected(key.Row, key.KeyIndex);
            marker.Width = marker.Height = selected ? 13 : 10;
            marker.Fill = selected ? Brushes.Orange : Brushes.DeepSkyBlue;
            marker.StrokeThickness = selected ? 1.5 : 0.8;
            Canvas.SetTop(marker, AnimationTimelineRulerHeight + _animationEditorRows.IndexOf(key.Row) * AnimationTimelineRowHeight +
                (AnimationTimelineRowHeight - marker.Height) / 2);
        }
    }

    private double TimelineSeconds(double x) => _timelineStartSeconds + Math.Clamp((x - _timelineXStart) / _timelineXWidth, 0, 1) *
        (_timelineEndSeconds - _timelineStartSeconds);

    private double ClampDraftKeyTime(AnimChannel channel, int keyIndex, double seconds)
    {
        var animation = _animationEditorAnimation;
        if (animation == null)
            return seconds;
        var ticksPerSecond = channel.TicksPerSecond;
        var lowerTick = keyIndex == 0
            ? Math.Ceiling(_timelineStartSeconds * ticksPerSecond)
            : (double) channel.Times[keyIndex - 1] + 1;
        var upperTick = keyIndex + 1 >= channel.Times.Length
            ? Math.Floor(_timelineEndSeconds * ticksPerSecond)
            : (double) channel.Times[keyIndex + 1] - 1;
        if (lowerTick > upperTick)
            return channel.Times[keyIndex] / channel.TicksPerSecond;

        var targetTicks = seconds * ticksPerSecond;
        if (UserSettings.Default.AnimationSnapToDisplayRate && animation.DisplayRate > 0)
        {
            var grid = ticksPerSecond / animation.DisplayRate;
            if (grid > 0 && double.IsFinite(grid))
            {
                var firstGridPoint = Math.Ceiling(lowerTick / grid - 1e-10);
                var lastGridPoint = Math.Floor(upperTick / grid + 1e-10);
                if (firstGridPoint > lastGridPoint)
                    return channel.Times[keyIndex] / channel.TicksPerSecond;
                var requestedGridPoint = Math.Round(targetTicks / grid, MidpointRounding.AwayFromZero);
                targetTicks = Math.Round(Math.Clamp(requestedGridPoint, firstGridPoint, lastGridPoint) * grid, MidpointRounding.AwayFromZero);
            }
        }

        targetTicks = Math.Clamp(Math.Round(targetTicks, MidpointRounding.AwayFromZero), lowerTick, upperTick);
        return targetTicks / ticksPerSecond;
    }

    private void ScrubAnimation(double seconds)
    {
        var animation = _animationEditorAnimation;
        if (animation == null)
            return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            StopAnimationTimer();
            AnimationPlayButton.IsChecked = false;
            AnimationPlayButton.Content = "Play";
        }
        _player.Time = Math.Clamp(seconds, animation.StartSeconds, animation.EndSeconds);
        _syncingAnimationUi = true;
        AnimationSlider.Value = Math.Clamp(_player.Time, AnimationSlider.Minimum, AnimationSlider.Maximum);
        _syncingAnimationUi = false;
        ApplyAnimation();
    }

    private void UpdateAnimationTangentInputState()
    {
        var explicitTangents = AnimationKeyInterp.SelectedItem is ComboBoxItem { Tag: "Cubic" } &&
            AnimationKeyTangentMode.SelectedItem is ComboBoxItem { Tag: string mode } && mode is "User" or "Break";
        AnimationKeyArriveTangent.IsEnabled = explicitTangents && _selectedAnimationEditorRow != null;
        AnimationKeyLeaveTangent.IsEnabled = explicitTangents && _selectedAnimationEditorRow != null;
    }

    private void OnAnimationEditorTangentModeChanged(object sender, SelectionChangedEventArgs e) => UpdateAnimationTangentInputState();

    private async void OnApplyAnimationRangeClick(object sender, RoutedEventArgs e)
    {
        if (_animationEditorAnimation == null)
            return;
        if (!TryReadNumber(AnimationRangeStart.Text, out var start) || !TryReadNumber(AnimationRangeEnd.Text, out var end))
        {
            AnimationEditorStatus.Text = "Enter a valid start and end time in seconds.";
            return;
        }

        try
        {
            var result = await AnimationEditAgentTools.RunFromUiAsync(new JObject
            {
                ["blueprint"] = _viewModel.Entry.Path,
                ["animation"] = _animationEditorAnimation.Name,
                ["dry_run"] = false,
                ["operations"] = new JArray(new JObject
                {
                    ["op"] = "set_range", ["scope"] = "playback", ["start_seconds"] = start, ["end_seconds"] = end,
                }),
            });
            AnimationEditorStatus.Text = result;
        }
        catch (Exception exception)
        {
            AnimationEditorStatus.Text = exception.Message;
        }
    }

    private async void OnApplyAnimationKeyClick(object sender, RoutedEventArgs e)
    {
        var row = _selectedAnimationEditorRow;
        var animation = _animationEditorAnimation;
        var keyIndex = _selectedAnimationEditorKey;
        if (animation == null || row == null || keyIndex < 0 || keyIndex >= row.Channel.Keys.Length)
            return;
        if (!TryReadNumber(AnimationKeyTime.Text, out var time) || !TryReadNumber(AnimationKeyValue.Text, out var value) ||
            !TryReadEnum(AnimationKeyInterp, out KeyInterp interpolation) || !TryReadEnum(AnimationKeyTangentMode, out KeyTangentMode tangentMode))
        {
            AnimationEditorStatus.Text = "Enter a valid key time, value, interpolation and tangent mode.";
            return;
        }

        var current = row.Channel.Keys[keyIndex];
        var currentTime = row.Channel.Times[keyIndex] / row.Channel.TicksPerSecond;
        var operations = new JArray();
        if (Math.Abs(time - currentTime) > 0.25 / row.Channel.TicksPerSecond)
            operations.Add(CreateChannelOperation(row, keyIndex, "move_key", ("time_seconds", time)));
        if (interpolation != current.Interp || tangentMode != current.TangentMode)
            operations.Add(CreateChannelOperation(row, keyIndex, "set_interp", ("interp", interpolation.ToString()), ("tangent_mode", tangentMode.ToString())));
        if (Math.Abs(value - current.Value) > 0.0000001)
            operations.Add(CreateChannelOperation(row, keyIndex, "set_key", ("value", value)));

        if (interpolation == KeyInterp.Cubic && tangentMode is KeyTangentMode.User or KeyTangentMode.Break)
        {
            if (!TryReadNumber(AnimationKeyArriveTangent.Text, out var arrive) || !TryReadNumber(AnimationKeyLeaveTangent.Text, out var leave))
            {
                AnimationEditorStatus.Text = "Enter valid arrive and leave tangent slopes in value per second.";
                return;
            }
            var currentArrive = current.ArriveTangent * row.Channel.TicksPerSecond;
            var currentLeave = current.LeaveTangent * row.Channel.TicksPerSecond;
            if (tangentMode != current.TangentMode || Math.Abs(arrive - currentArrive) > 0.0000001 || Math.Abs(leave - currentLeave) > 0.0000001)
                operations.Add(CreateChannelOperation(row, keyIndex, "set_tangent",
                    ("arrive_per_second", arrive), ("leave_per_second", leave), ("tangent_mode", tangentMode.ToString()),
                    ("weight_mode", current.WeightMode.ToString()), ("arrive_weight", current.ArriveWeight), ("leave_weight", current.LeaveWeight)));
        }

        if (operations.Count == 0)
        {
            AnimationEditorStatus.Text = "No key changes to apply.";
            return;
        }

        try
        {
            var result = await AnimationEditAgentTools.RunFromUiAsync(new JObject
            {
                ["blueprint"] = _viewModel.Entry.Path,
                ["animation"] = animation.Name,
                ["dry_run"] = false,
                ["operations"] = operations,
            });
            AnimationEditorStatus.Text = result;
        }
        catch (Exception exception)
        {
            AnimationEditorStatus.Text = exception.Message;
        }
    }

    private static JObject CreateChannelOperation(AnimationEditorRow row, int keyIndex, string op, params (string Name, object Value)[] values)
    {
        var operation = new JObject
        {
            ["op"] = op,
            ["widget"] = row.Widget,
            ["track_class"] = row.TrackClass,
            ["property"] = row.Property,
            ["section"] = row.Section,
            ["channel"] = row.Channel.Name,
            ["key"] = keyIndex,
        };
        foreach (var (name, value) in values)
            operation[name] = JToken.FromObject(value);
        return operation;
    }

    private static bool TryReadNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.Float, AnimationEditorCulture, out number) && double.IsFinite(number);

    private static bool TryReadEnum<TEnum>(ComboBox combo, out TEnum value) where TEnum : struct, Enum => TryReadSelectedEnum(combo, out value);
}
