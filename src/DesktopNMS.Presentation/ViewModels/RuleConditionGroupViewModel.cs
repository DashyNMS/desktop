using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One AND/OR group in the rule builder's condition tree (issue #22) - the
/// same "Add rule / Add group" nesting LibreNMS's own jQuery QueryBuilder
/// editor offers. <see cref="Children"/> mixes <see cref="RuleConditionRowViewModel"/>
/// leaves and nested groups; the view picks a template per type.
/// </summary>
public sealed class RuleConditionGroupViewModel : ObservableObject
{
    private readonly RuleConditionGroupViewModel? _parent;
    private string _condition = "AND";

    public RuleConditionGroupViewModel(RuleConditionGroupViewModel? parent = null)
    {
        _parent = parent;
        Children = new ObservableCollection<object>();

        AddRuleCommand = new RelayCommand(() => Children.Add(new RuleConditionRowViewModel(RemoveChild)));
        AddGroupCommand = new RelayCommand(() =>
        {
            var group = new RuleConditionGroupViewModel(this);
            group.Children.Add(new RuleConditionRowViewModel(group.RemoveChild));
            Children.Add(group);
        });
        RemoveCommand = new RelayCommand(() => _parent?.RemoveChild(this), () => _parent is not null);
    }

    /// <summary>Builds a tree from a stored builder node; a leaf handed in is wrapped in an AND group.</summary>
    public static RuleConditionGroupViewModel FromNode(AlertConditionNode node, RuleConditionGroupViewModel? parent = null)
    {
        var group = new RuleConditionGroupViewModel(parent);
        group.ReplaceWith(node);
        return group;
    }

    /// <summary>Replaces everything below this group with <paramref name="node"/>'s tree - the import commands' entry point on the root.</summary>
    public void ReplaceWith(AlertConditionNode node)
    {
        Children.Clear();

        if (!node.IsGroup)
        {
            Condition = "AND";
            Children.Add(RuleConditionRowViewModel.FromNode(node, RemoveChild));
            return;
        }

        Condition = string.Equals(node.Condition, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";

        foreach (var child in node.Rules!)
        {
            Children.Add(child.IsGroup
                ? FromNode(child, this)
                : RuleConditionRowViewModel.FromNode(child, RemoveChild));
        }

        if (Children.Count == 0)
        {
            Children.Add(new RuleConditionRowViewModel(RemoveChild));
        }
    }

    public ObservableCollection<object> Children { get; }

    public bool IsRoot => _parent is null;

    public RuleConditionGroupViewModel? Parent => _parent;

    /// <summary>True if <paramref name="group"/> is this group or any ancestor of it - a group can't be dropped inside itself.</summary>
    public bool IsWithin(RuleConditionGroupViewModel group)
    {
        for (var current = this; current is not null; current = current._parent)
        {
            if (ReferenceEquals(current, group))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The group directly containing <paramref name="child"/>, searching this subtree.</summary>
    public RuleConditionGroupViewModel? FindOwner(object child)
    {
        if (Children.Contains(child))
        {
            return this;
        }

        foreach (var nested in Children.OfType<RuleConditionGroupViewModel>())
        {
            var owner = nested.FindOwner(child);
            if (owner is not null)
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a child (row or nested group) belonging to this group from a
    /// node - used by drag-to-reorder, which moves a child by rebuilding it
    /// in its new group so its remove callback and parent link are right.
    /// </summary>
    public object Adopt(AlertConditionNode node) => node.IsGroup
        ? FromNode(node, this)
        : RuleConditionRowViewModel.FromNode(node, RemoveChild);

    /// <summary>
    /// Removes a child for a move - unlike <see cref="RemoveChild"/> there is
    /// no keep-one-row guard on the root (the caller has already checked the
    /// move leaves it non-empty), but an emptied sub-group is still pruned.
    /// </summary>
    public void Detach(object child)
    {
        Children.Remove(child);

        if (Children.Count == 0 && _parent is not null)
        {
            _parent.Detach(this);
        }
    }

    /// <summary>
    /// A unique RadioButton GroupName per group, so each nested group's
    /// AND/OR pair toggles independently - WPF links every RadioButton in a
    /// window that shares a GroupName, which with nesting would otherwise
    /// make choosing OR on a sub-group un-tick AND on the root.
    /// </summary>
    public string RadioGroupName { get; } = Guid.NewGuid().ToString("N");

    /// <summary>"AND" or "OR".</summary>
    public string Condition
    {
        get => _condition;
        set
        {
            if (SetProperty(ref _condition, value))
            {
                OnPropertyChanged(nameof(IsAnd));
                OnPropertyChanged(nameof(IsOr));
            }
        }
    }

    public bool IsAnd
    {
        get => Condition == "AND";
        set { if (value) Condition = "AND"; }
    }

    public bool IsOr
    {
        get => Condition == "OR";
        set { if (value) Condition = "OR"; }
    }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand AddGroupCommand { get; }

    /// <summary>Removes this group from its parent; disabled on the root.</summary>
    public RelayCommand RemoveCommand { get; }

    public AlertConditionNode ToNode() => new()
    {
        Condition = Condition,
        Rules = Children.Select(c => c switch
        {
            RuleConditionRowViewModel row => row.ToNode(),
            RuleConditionGroupViewModel group => group.ToNode(),
            _ => throw new InvalidOperationException($"Unexpected condition child {c.GetType().Name}"),
        }).ToList(),
        Valid = true,
    };

    private void RemoveChild(object child)
    {
        // The root always keeps at least one row so there's something to
        // edit; an emptied sub-group is just removed along with its last
        // member, the same as LibreNMS's own builder behaves.
        if (IsRoot && Children.Count <= 1)
        {
            return;
        }

        Children.Remove(child);

        if (Children.Count == 0)
        {
            _parent?.RemoveChild(this);
        }
    }
}
