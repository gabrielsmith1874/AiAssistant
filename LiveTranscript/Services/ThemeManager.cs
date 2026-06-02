using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LiveTranscript.Services
{
    public static class ThemeManager
    {
        public static readonly IReadOnlyList<(string Id, string Name)> Themes = new List<(string, string)>
        {
            ("Original", "Original"),
            ("GraphiteFrost", "Graphite Frost"),
            ("MidnightAurora", "Midnight Aurora"),
            ("EmberSlate", "Ember Slate"),
            ("ArcticGlass", "Arctic Glass")
        };

        private static readonly Dictionary<string, ThemePalette> Palettes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Original"] = new ThemePalette(
                BgDark: "#FF070B14",
                BgPanel: "#CC0F1A2A",
                BgSurface: "#7A1D2A3E",
                BgHover: "#9930425D",
                Border: "#66A6BDD8",
                TextPrimary: "#FFF4F8FF",
                TextSecondary: "#FF9AAAC1",
                AccentCyan: "#FF53D3FF",
                AccentRed: "#FFFF6B9D",
                AccentGreen: "#FF56D364",
                RootShell: "#B30A1423",
                RootBorder: "#66BFD6EF",
                TitleBar: "#8C102238",
                ControlsBar: "#66162A42",
                Panel: "#141F3048",
                PanelBorder: "#40A6BDD8",
                Backdrop: "#1AFFFFFF",
                BackdropBorder: "#33D7EEFF",
                Card: "#2BFFFFFF",
                QaCard: "#24FFFFFF",
                Answer: "#1FFFFFFF",
                FollowUp: "#22FFFFFF",
                FollowUpAnswer: "#18FFFFFF",
                CardBorder: "#26D7EEFF",
                AnswerBorder: "#20D7EEFF",
                FollowUpBorder: "#22D7EEFF",
                FollowUpAnswerBorder: "#1FD7EEFF",
                Splitter: "#2AA6BDD8",
                Indicator: "#2EFFFFFF",
                PrimaryButton: "#FF00D2D3",
                PrimaryButton2: "#FF0099CC",
                PrimaryButtonText: "#FF0D1117",
                ExtractButton: "#FF9B59B6",
                ExtractButton2: "#FF8E44AD",
                ExtractButtonText: "#FFFFFFFF",
                Badge: "#FF9B59B6",
                FollowUpBadge: "#FF4FA3D1",
                HudHeader: "#CC000000",
                HudPanel: "#66000000",
                SettingsPanel: "#D2111A26",
                StatusBar: "#4D1A2636",
                Shadow: "#FF05080E"),

            ["GraphiteFrost"] = new ThemePalette(
                BgDark: "#FF07090D",
                BgPanel: "#D0101115",
                BgSurface: "#36FFFFFF",
                BgHover: "#52FFFFFF",
                Border: "#30FFFFFF",
                TextPrimary: "#FFF5F5F7",
                TextSecondary: "#FFB7BAC1",
                AccentCyan: "#FFE8E8ED",
                AccentRed: "#FFF5F5F7",
                AccentGreen: "#FFD1D1D6",
                RootShell: "#C80E0F13",
                RootBorder: "#36FFFFFF",
                TitleBar: "#2FFFFFFF",
                ControlsBar: "#22FFFFFF",
                Panel: "#26FFFFFF",
                PanelBorder: "#2EFFFFFF",
                Backdrop: "#18FFFFFF",
                BackdropBorder: "#24FFFFFF",
                Card: "#24FFFFFF",
                QaCard: "#20FFFFFF",
                Answer: "#17FFFFFF",
                FollowUp: "#1DFFFFFF",
                FollowUpAnswer: "#12FFFFFF",
                CardBorder: "#20FFFFFF",
                AnswerBorder: "#1AFFFFFF",
                FollowUpBorder: "#1DFFFFFF",
                FollowUpAnswerBorder: "#16FFFFFF",
                Splitter: "#24FFFFFF",
                Indicator: "#30FFFFFF",
                PrimaryButton: "#FFF5F5F7",
                PrimaryButton2: "#FFD1D1D6",
                PrimaryButtonText: "#FF111215",
                ExtractButton: "#FFF5F5F7",
                ExtractButton2: "#FFD7D7DC",
                ExtractButtonText: "#FF111215",
                Badge: "#FFE8E8ED",
                FollowUpBadge: "#FFB8BBC3",
                HudHeader: "#CC111114",
                HudPanel: "#A6111114",
                SettingsPanel: "#E6101114",
                StatusBar: "#22FFFFFF",
                Shadow: "#FF030405"),

            ["MidnightAurora"] = new ThemePalette(
                BgDark: "#FF050A14",
                BgPanel: "#D00C1625",
                BgSurface: "#45283A56",
                BgHover: "#663B577A",
                Border: "#665D84B5",
                TextPrimary: "#FFF3F7FF",
                TextSecondary: "#FFAFC0D5",
                AccentCyan: "#FF7AD7FF",
                AccentRed: "#FFFF7AB5",
                AccentGreen: "#FF72E0C3",
                RootShell: "#C708101E",
                RootBorder: "#706EA8D9",
                TitleBar: "#74203754",
                ControlsBar: "#5C172844",
                Panel: "#24243A58",
                PanelBorder: "#4D6EA8D9",
                Backdrop: "#202D4668",
                BackdropBorder: "#426EA8D9",
                Card: "#303B5577",
                QaCard: "#2A314C70",
                Answer: "#222A405F",
                FollowUp: "#293F587A",
                FollowUpAnswer: "#1F304A6B",
                CardBorder: "#3B8BC8F1",
                AnswerBorder: "#2C8BC8F1",
                FollowUpBorder: "#36BFA7FF",
                FollowUpAnswerBorder: "#29BFA7FF",
                Splitter: "#3A7AD7FF",
                Indicator: "#342F5F83",
                PrimaryButton: "#FF7AD7FF",
                PrimaryButton2: "#FF55A7FF",
                PrimaryButtonText: "#FF06111B",
                ExtractButton: "#FFB78CFF",
                ExtractButton2: "#FF6FA8FF",
                ExtractButtonText: "#FFFFFFFF",
                Badge: "#FFB78CFF",
                FollowUpBadge: "#FF62D1F1",
                HudHeader: "#D008101E",
                HudPanel: "#9E08101E",
                SettingsPanel: "#E00B1424",
                StatusBar: "#4D172844",
                Shadow: "#FF020611"),

            ["EmberSlate"] = new ThemePalette(
                BgDark: "#FF120D0B",
                BgPanel: "#D01B1512",
                BgSurface: "#4A3A2A21",
                BgHover: "#66584132",
                Border: "#737E6555",
                TextPrimary: "#FFFFF7EF",
                TextSecondary: "#FFD4BDAA",
                AccentCyan: "#FFFFC680",
                AccentRed: "#FFFF8D68",
                AccentGreen: "#FFEBCB88",
                RootShell: "#C7130E0B",
                RootBorder: "#807E6555",
                TitleBar: "#78332620",
                ControlsBar: "#6631241D",
                Panel: "#273E2E24",
                PanelBorder: "#577E6555",
                Backdrop: "#213E2E24",
                BackdropBorder: "#507E6555",
                Card: "#333F2E24",
                QaCard: "#2C3A2A21",
                Answer: "#2332241C",
                FollowUp: "#2D432F24",
                FollowUpAnswer: "#2235271E",
                CardBorder: "#4C9B7B55",
                AnswerBorder: "#379B7B55",
                FollowUpBorder: "#44D5955B",
                FollowUpAnswerBorder: "#32D5955B",
                Splitter: "#3FC79672",
                Indicator: "#383F2E24",
                PrimaryButton: "#FFFFC680",
                PrimaryButton2: "#FFFF986B",
                PrimaryButtonText: "#FF1A0F09",
                ExtractButton: "#FFFF9E6E",
                ExtractButton2: "#FFFF725D",
                ExtractButtonText: "#FF1A0F09",
                Badge: "#FFFF9E6E",
                FollowUpBadge: "#FFFFC680",
                HudHeader: "#D00F0A08",
                HudPanel: "#A60F0A08",
                SettingsPanel: "#E61A110D",
                StatusBar: "#5931241D",
                Shadow: "#FF070302"),

            ["ArcticGlass"] = new ThemePalette(
                BgDark: "#FF081018",
                BgPanel: "#D0121C27",
                BgSurface: "#4CDAF0FF",
                BgHover: "#66E9F6FF",
                Border: "#80C7E8FF",
                TextPrimary: "#FFF8FCFF",
                TextSecondary: "#FFC4D4E1",
                AccentCyan: "#FFAEE8FF",
                AccentRed: "#FFFFB8C7",
                AccentGreen: "#FFB8F4DA",
                RootShell: "#C80D1620",
                RootBorder: "#80C7E8FF",
                TitleBar: "#61345063",
                ControlsBar: "#40345063",
                Panel: "#243B5B74",
                PanelBorder: "#66BBDFFF",
                Backdrop: "#1FCBEAFF",
                BackdropBorder: "#55C7E8FF",
                Card: "#30DDF3FF",
                QaCard: "#2ADDF3FF",
                Answer: "#20DDF3FF",
                FollowUp: "#24DDF3FF",
                FollowUpAnswer: "#18DDF3FF",
                CardBorder: "#55DDF3FF",
                AnswerBorder: "#3FDDF3FF",
                FollowUpBorder: "#48DDF3FF",
                FollowUpAnswerBorder: "#34DDF3FF",
                Splitter: "#55C7E8FF",
                Indicator: "#38E9F6FF",
                PrimaryButton: "#FFE9F6FF",
                PrimaryButton2: "#FFAEDCFF",
                PrimaryButtonText: "#FF07131C",
                ExtractButton: "#FFC8E8FF",
                ExtractButton2: "#FF8EC6FF",
                ExtractButtonText: "#FF07131C",
                Badge: "#FFC8E8FF",
                FollowUpBadge: "#FFAEE8FF",
                HudHeader: "#D00B1722",
                HudPanel: "#A60B1722",
                SettingsPanel: "#E60D1721",
                StatusBar: "#44345063",
                Shadow: "#FF020711")
        };

        public static string NormalizeThemeId(string? themeId) =>
            !string.IsNullOrWhiteSpace(themeId) && Palettes.ContainsKey(themeId)
                ? themeId
                : "Original";

        public static string GetThemeName(string? themeId)
        {
            var id = NormalizeThemeId(themeId);
            foreach (var theme in Themes)
            {
                if (theme.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return theme.Name;
            }
            return "Original";
        }

        public static void Apply(MainWindow window, string? themeId, bool hudEnabled)
        {
            var palette = Palettes[NormalizeThemeId(themeId)];
            var appResources = Application.Current.Resources;

            SetBrush(appResources, "BgDarkBrush", palette.BgDark);
            SetBrush(appResources, "BgPanelBrush", palette.BgPanel);
            SetBrush(appResources, "BgSurfaceBrush", palette.BgSurface);
            SetBrush(appResources, "BgHoverBrush", palette.BgHover);
            SetBrush(appResources, "BorderBrush", palette.Border);
            SetBrush(appResources, "TextPrimaryBrush", palette.TextPrimary);
            SetBrush(appResources, "TextSecondaryBrush", palette.TextSecondary);
            SetBrush(appResources, "AccentCyanBrush", palette.AccentCyan);
            SetBrush(appResources, "AccentRedBrush", palette.AccentRed);
            SetBrush(appResources, "AccentGreenBrush", palette.AccentGreen);

            SetBrush(window.Resources, "RootShellBrush", palette.RootShell);
            SetBrush(window.Resources, "RootShellBorderBrush", palette.RootBorder);
            SetBrush(window.Resources, "TitleBarBrush", palette.TitleBar);
            SetBrush(window.Resources, "ControlsBarBrush", palette.ControlsBar);
            SetBrush(window.Resources, "PanelBrush", palette.Panel);
            SetBrush(window.Resources, "PanelBorderBrush", palette.PanelBorder);
            SetBrush(window.Resources, "PanelBackdropBrush", hudEnabled ? "#00000000" : palette.Backdrop);
            SetBrush(window.Resources, "PanelBackdropBorderBrush", hudEnabled ? "#00000000" : palette.BackdropBorder);
            SetBrush(window.Resources, "TranscriptCardBackgroundBrush", hudEnabled ? palette.HudPanel : palette.Card);
            SetBrush(window.Resources, "QaCardBackgroundBrush", hudEnabled ? palette.HudPanel : palette.QaCard);
            SetBrush(window.Resources, "QaAnswerBackgroundBrush", hudEnabled ? palette.HudHeader : palette.Answer);
            SetBrush(window.Resources, "FollowUpCardBackgroundBrush", hudEnabled ? palette.HudPanel : palette.FollowUp);
            SetBrush(window.Resources, "FollowUpAnswerBackgroundBrush", hudEnabled ? palette.HudHeader : palette.FollowUpAnswer);
            SetBrush(window.Resources, "CardBorderBrush", hudEnabled ? "#30FFFFFF" : palette.CardBorder);
            SetBrush(window.Resources, "AnswerBorderBrush", hudEnabled ? "#28FFFFFF" : palette.AnswerBorder);
            SetBrush(window.Resources, "FollowUpBorderBrush", hudEnabled ? "#2CFFFFFF" : palette.FollowUpBorder);
            SetBrush(window.Resources, "FollowUpAnswerBorderBrush", hudEnabled ? "#22FFFFFF" : palette.FollowUpAnswerBorder);
            SetBrush(window.Resources, "SplitterBrush", palette.Splitter);
            SetBrush(window.Resources, "ClaudeIndicatorBrush", palette.Indicator);
            SetBrush(window.Resources, "PrimaryButtonBrush", palette.PrimaryButton);
            SetBrush(window.Resources, "PrimaryButtonAltBrush", palette.PrimaryButton2);
            SetBrush(window.Resources, "PrimaryButtonTextBrush", palette.PrimaryButtonText);
            SetBrush(window.Resources, "ExtractButtonBrush", palette.ExtractButton);
            SetBrush(window.Resources, "ExtractButtonAltBrush", palette.ExtractButton2);
            SetBrush(window.Resources, "ExtractButtonTextBrush", palette.ExtractButtonText);
            SetBrush(window.Resources, "QuestionBadgeBrush", palette.Badge);
            SetBrush(window.Resources, "FollowUpBadgeBrush", palette.FollowUpBadge);
            SetBrush(window.Resources, "HudHeaderBrush", palette.HudHeader);
            SetBrush(window.Resources, "HudPanelBrush", palette.HudPanel);
            SetBrush(window.Resources, "SettingsPanelBrush", palette.SettingsPanel);
            SetBrush(window.Resources, "StatusBarBrush", palette.StatusBar);

            window.RootShell.Margin = hudEnabled ? new Thickness(0) : new Thickness(8);
            window.RootShell.CornerRadius = hudEnabled ? new CornerRadius(0) : new CornerRadius(16);
            window.RootShell.Background = hudEnabled ? Brushes.Transparent : Brush(palette.RootShell);
            window.RootShell.BorderBrush = hudEnabled ? Brushes.Transparent : Brush(palette.RootBorder);
            window.RootShell.BorderThickness = hudEnabled ? new Thickness(0) : new Thickness(1);
            window.RootShell.Effect = hudEnabled
                ? null
                : new DropShadowEffect
                {
                    BlurRadius = 36,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = (Color)ColorConverter.ConvertFromString(palette.Shadow)
                };

            window.StartStopButton.Background = Gradient(palette.PrimaryButton, palette.PrimaryButton2);
            window.StartStopButton.Foreground = Brush(palette.PrimaryButtonText);
            window.ExtractButton.Background = Gradient(palette.ExtractButton, palette.ExtractButton2);
            window.ExtractButton.Foreground = Brush(palette.ExtractButtonText);
            window.HudExtractButton.Background = Gradient(palette.ExtractButton, palette.ExtractButton2);
            window.HudExtractButton.Foreground = Brush(palette.ExtractButtonText);
            window.TranscriptPanelBackdrop.Background = hudEnabled ? Brushes.Transparent : Brush(palette.Backdrop);
            window.TranscriptPanelBackdrop.BorderBrush = hudEnabled ? Brushes.Transparent : Brush(palette.BackdropBorder);
            window.QaPanelBackdrop.Background = hudEnabled ? Brushes.Transparent : Brush(palette.Backdrop);
            window.QaPanelBackdrop.BorderBrush = hudEnabled ? Brushes.Transparent : Brush(palette.BackdropBorder);
        }

        private static Brush Brush(string color) => new SolidColorBrush(ParseColor(color));

        private static LinearGradientBrush Gradient(string start, string end) =>
            new(ParseColor(start), ParseColor(end), 45);

        private static void SetBrush(ResourceDictionary resources, string key, string color)
        {
            var parsedColor = ParseColor(color);
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = parsedColor;
            }
            else
            {
                resources[key] = new SolidColorBrush(parsedColor);
            }
        }

        private static Color ParseColor(string color) =>
            (Color)ColorConverter.ConvertFromString(color);

        private sealed record ThemePalette(
            string BgDark,
            string BgPanel,
            string BgSurface,
            string BgHover,
            string Border,
            string TextPrimary,
            string TextSecondary,
            string AccentCyan,
            string AccentRed,
            string AccentGreen,
            string RootShell,
            string RootBorder,
            string TitleBar,
            string ControlsBar,
            string Panel,
            string PanelBorder,
            string Backdrop,
            string BackdropBorder,
            string Card,
            string QaCard,
            string Answer,
            string FollowUp,
            string FollowUpAnswer,
            string CardBorder,
            string AnswerBorder,
            string FollowUpBorder,
            string FollowUpAnswerBorder,
            string Splitter,
            string Indicator,
            string PrimaryButton,
            string PrimaryButton2,
            string PrimaryButtonText,
            string ExtractButton,
            string ExtractButton2,
            string ExtractButtonText,
            string Badge,
            string FollowUpBadge,
            string HudHeader,
            string HudPanel,
            string SettingsPanel,
            string StatusBar,
            string Shadow);
    }
}
