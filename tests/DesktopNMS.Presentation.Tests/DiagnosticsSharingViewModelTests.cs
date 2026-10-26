using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DiagnosticsSharingViewModelTests
{
    [Fact]
    public void Starts_with_unchecked_choices_and_requires_a_selection_to_allow()
    {
        var viewModel = new DiagnosticsSharingViewModel();
        var closed = false;
        viewModel.RequestClose += (_, _) => closed = true;
        Assert.False(viewModel.ShareErrors);
        Assert.False(viewModel.ShareUsage);
        Assert.Null(viewModel.Choice);
        Assert.False(viewModel.AllowSelectedCommand.CanExecute(null));
        Assert.True(viewModel.DeclineCommand.CanExecute(null));
        viewModel.AllowSelectedCommand.Execute(null);
        Assert.Null(viewModel.Choice);
        Assert.False(closed);
    }

    [Theory]
    [InlineData(true, false, DiagnosticsSharingPreference.ErrorsOnly)]
    [InlineData(false, true, DiagnosticsSharingPreference.UsageOnly)]
    [InlineData(true, true, DiagnosticsSharingPreference.ErrorsAndUsage)]
    public void Allows_exactly_the_selected_categories(bool errors, bool usage, DiagnosticsSharingPreference expected)
    {
        var viewModel = new DiagnosticsSharingViewModel { ShareErrors = errors, ShareUsage = usage };
        DiagnosticsSharingPreference? closedWith = null;
        viewModel.RequestClose += (_, choice) => closedWith = choice;
        Assert.True(viewModel.AllowSelectedCommand.CanExecute(null));
        viewModel.AllowSelectedCommand.Execute(null);
        Assert.Equal(expected, viewModel.Choice);
        Assert.Equal(expected, closedWith);
        Assert.False(viewModel.AllowSelectedCommand.CanExecute(null));
        Assert.False(viewModel.DeclineCommand.CanExecute(null));
    }

    [Fact]
    public void Clearing_the_last_checked_category_disables_allow_again()
    {
        var viewModel = new DiagnosticsSharingViewModel();
        viewModel.ShareErrors = true;
        Assert.True(viewModel.AllowSelectedCommand.CanExecute(null));
        viewModel.ShareUsage = true;
        viewModel.ShareErrors = false;
        Assert.True(viewModel.AllowSelectedCommand.CanExecute(null));
        viewModel.ShareUsage = false;
        Assert.False(viewModel.AllowSelectedCommand.CanExecute(null));
        Assert.Null(viewModel.Choice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Explicit_decline_always_selects_no_sharing(bool errors, bool usage)
    {
        var viewModel = new DiagnosticsSharingViewModel { ShareErrors = errors, ShareUsage = usage };
        DiagnosticsSharingPreference? closedWith = null;
        viewModel.RequestClose += (_, choice) => closedWith = choice;
        viewModel.DeclineCommand.Execute(null);
        Assert.Equal(DiagnosticsSharingPreference.None, viewModel.Choice);
        Assert.Equal(DiagnosticsSharingPreference.None, closedWith);
        Assert.False(viewModel.AllowSelectedCommand.CanExecute(null));
        Assert.False(viewModel.DeclineCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_first_decision_cannot_be_overwritten_or_close_twice(bool allowFirst)
    {
        var viewModel = new DiagnosticsSharingViewModel { ShareErrors = true };
        var choices = new List<DiagnosticsSharingPreference>();
        viewModel.RequestClose += (_, choice) => choices.Add(choice);
        if (allowFirst) viewModel.AllowSelectedCommand.Execute(null);
        else viewModel.DeclineCommand.Execute(null);
        viewModel.ShareErrors = false;
        viewModel.ShareUsage = true;
        viewModel.AllowSelectedCommand.Execute(null);
        viewModel.DeclineCommand.Execute(null);
        viewModel.AllowSelectedCommand.Execute(null);
        var expected = allowFirst ? DiagnosticsSharingPreference.ErrorsOnly : DiagnosticsSharingPreference.None;
        Assert.Equal(expected, viewModel.Choice);
        Assert.Equal(expected, Assert.Single(choices));
    }
}
