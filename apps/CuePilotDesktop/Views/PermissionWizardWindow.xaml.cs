using System.Windows;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views;

public partial class PermissionWizardWindow : Window
{
    private const int LastStepIndex = 2;
    private readonly LocalizationService _localizer;

    private int _currentStep;

    public PermissionWizardWindow(LocalizationService localizer)
    {
        _localizer = localizer;
        InitializeComponent();
        _localizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                RenderStep();
            }
        };
        RenderStep();
    }

    private void RenderStep()
    {
        WizardStep[] steps =
        [
            new(
                _localizer["WizardStep1Title"],
                _localizer["WizardStep1Description"]),
            new(
                _localizer["WizardStep2Title"],
                _localizer["WizardStep2Description"]),
            new(
                _localizer["WizardStep3Title"],
                _localizer["WizardStep3Description"])
        ];

        var step = steps[_currentStep];
        Title = _localizer["PermissionWizardTitle"];
        StepTitleText.Text = step.Title;
        StepDescriptionText.Text = step.Description;
        StepCounterText.Text = string.Format(_localizer["WizardStepCounter"], _currentStep + 1, steps.Length);
        ConfirmCheckBox.Content = _localizer["WizardConfirm"];
        BackButton.Content = _localizer["WizardBack"];
        NextButton.Content = _localizer["WizardNext"];
        FinishButton.Content = _localizer["WizardFinish"];

        BackButton.IsEnabled = _currentStep > 0;
        NextButton.Visibility = _currentStep < steps.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _currentStep == steps.Length - 1 ? Visibility.Visible : Visibility.Collapsed;

        ConfirmCheckBox.IsChecked = false;
        NextButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep <= 0)
        {
            return;
        }

        _currentStep--;
        RenderStep();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep >= LastStepIndex)
        {
            return;
        }

        _currentStep++;
        RenderStep();
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
    {
        bool confirmed = ConfirmCheckBox.IsChecked == true;
        if (_currentStep < LastStepIndex)
        {
            NextButton.IsEnabled = confirmed;
        }
        else
        {
            FinishButton.IsEnabled = confirmed;
        }
    }

    private record WizardStep(string Title, string Description);
}
