using Avalonia.Controls;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views.Dialogs;

public partial class PermissionWizardWindow : Window
{
    private const int LastStepIndex = 2;
    private readonly LocalizationService _localizer = null!;

    private int _currentStep;

    public PermissionWizardWindow()
    {
        InitializeComponent();
    }

    public PermissionWizardWindow(LocalizationService localizer)
        : this()
    {
        _localizer = localizer;
        _localizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                RenderStep();
            }
        };
        BackButton.Click += (_, _) => OnBackClick();
        NextButton.Click += (_, _) => OnNextClick();
        FinishButton.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };
        ConfirmCheckBox.IsCheckedChanged += (_, _) => OnConfirmChanged();
        RenderStep();
    }

    public bool Confirmed { get; private set; }

    public static bool Run(Window? owner, LocalizationService localizer)
    {
        var wizard = new PermissionWizardWindow(localizer);
        ModalRunner.Run(wizard, owner);
        return wizard.Confirmed;
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
        NextButton.IsVisible = _currentStep < steps.Length - 1;
        FinishButton.IsVisible = _currentStep == steps.Length - 1;

        ConfirmCheckBox.IsChecked = false;
        NextButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
    }

    private void OnBackClick()
    {
        if (_currentStep <= 0)
        {
            return;
        }

        _currentStep--;
        RenderStep();
    }

    private void OnNextClick()
    {
        if (_currentStep >= LastStepIndex)
        {
            return;
        }

        _currentStep++;
        RenderStep();
    }

    private void OnConfirmChanged()
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
