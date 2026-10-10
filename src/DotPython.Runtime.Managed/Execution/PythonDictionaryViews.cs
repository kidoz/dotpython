// The set algebra of dictionary views follows CPython 3.14.7 Objects/dictobject.c
// (dictviews_*) and Lib/_collections_abc.py (Set):
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// What a dictionary view answers beyond iteration: `keys()` and `items()` are set-like, so
/// they take the four set operators, the four orderings and `isdisjoint`, while `values()` is
/// a plain iterable whose equality is identity.
/// </summary>
/// <remarks>
/// The operators always build a plain `set` and take any iterable on the other side — the
/// views are not sets themselves and are unhashable — while the orderings, like CPython's,
/// accept only a set-like operand. Equality is the set comparison for `keys()`/`items()`
/// against anything set-like and `False` against everything else, never `NotImplemented`.
/// </remarks>
internal static class PythonDictionaryViews
{
    /// <summary>Whether a view is set-like: the keys and items views are, values is not.</summary>
    internal static bool IsSetLike(PythonValue value) =>
        value is PythonDictionaryViewValue { Kind: "dict_keys" or "dict_items" };

    /// <summary>Whether a value is set-like: a set, a frozenset, or a set-like view.</summary>
    internal static bool IsSetLikeOperand(PythonValue value) =>
        value is PythonSetValue || IsSetLike(value);

    /// <summary>The view's contents as a set, which is what every operator contributes.</summary>
    internal static PythonSetValue AsSet(PythonValue value, TextSpan span) =>
        value is PythonSetValue set
            ? set.Copy(frozen: false, span: span)
            : PythonSetOperations.Create(value, span);

    /// <summary>
    /// `&`, `|`, `-` and `^` where at least one side is a set-like view: the result is a set
    /// and the other operand may be any iterable, while a value that cannot be iterated
    /// reports the refusal its conversion raises. A view on the right of a set — `{1} |
    /// d.keys()` — answers through its own reflected slot the way CPython's does.
    /// </summary>
    internal static bool TryApplySetOperation(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        var operation = opCode switch
        {
            PythonOpCode.BinaryAnd => PythonSetOperations.Operation.Intersection,
            PythonOpCode.BinaryOr => PythonSetOperations.Operation.Union,
            PythonOpCode.BinarySubtract => PythonSetOperations.Operation.Difference,
            PythonOpCode.BinaryXor => PythonSetOperations.Operation.SymmetricDifference,
            _ => (PythonSetOperations.Operation?)null,
        };
        if (operation is not { } selected)
            return false;
        // Whichever side is the view answers, but the operands keep their places: a
        // reflected `__rsub__` still computes `set - view`.
        PythonValue view;
        if (IsSetLike(left))
            view = left;
        else if (IsSetLike(right) && IsSetLikeOperand(left))
            view = right;
        else
            return false;
        var other = ReferenceEquals(view, right) ? left : right;
        var viewSet = AsSet(view, span);
        if (ReferenceEquals(view, left))
        {
            if (
                selected
                is PythonSetOperations.Operation.Intersection
                    or PythonSetOperations.Operation.Difference
            )
            {
                // These two read the other operand through the view, so an element that
                // cannot be a dictionary key is refused the way the dictionary refuses it.
                var kept = new List<PythonValue>();
                var iterator = ManagedObjectProtocols.GetIterator(other, span);
                while (ManagedObjectProtocols.TryGetNext(iterator, out var element, span))
                {
                    if (ManagedObjectProtocols.Contains(view, element, span))
                        kept.Add(element);
                }
                result = PythonSetOperations.Combine(
                    viewSet,
                    new PythonSetValue(kept),
                    selected,
                    span
                );
                return true;
            }
            result = PythonSetOperations.Combine(viewSet, AsSet(other, span), selected, span);
            return true;
        }
        // The view is the right operand: its reflected slot answers with the operands in
        // their original places, so `{…} - d.keys()` is still a set minus the keys.
        result = PythonSetOperations.Combine(AsSet(other, span), viewSet, selected, span);
        return true;
    }

    /// <summary>
    /// The four orderings, which a view answers against a set, a frozenset or another
    /// set-like view, and refuses for anything else — a list and even a dict with the same
    /// keys report the same "not supported between instances" wording a set reports.
    /// </summary>
    internal static bool TryCompareOrdered(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison,
        TextSpan span,
        out bool result
    )
    {
        result = false;
        if (!IsSetLike(left) && !IsSetLike(right))
            return false;
        if (!IsSetLikeOperand(left) || !IsSetLikeOperand(right))
            return false;
        result = ManagedObjectProtocols.IsTrue(
            PythonSetOperations.CompareOrdered(
                AsSet(left, span),
                AsSet(right, span),
                comparison,
                span
            )
        );
        return true;
    }

    /// <summary>
    /// `==`/`!=` for a keys or items view: the set comparison against another set-like value,
    /// and `False` against everything else — a list, a tuple, or a dict of the same entries.
    /// A values view keeps the identity answer `object` gives.
    /// </summary>
    internal static bool TryEquality(PythonValue left, PythonValue right, out bool equal)
    {
        equal = false;
        if (left is not PythonDictionaryViewValue { Kind: "dict_keys" or "dict_items" })
            return false;
        if (!IsSetLikeOperand(right))
        {
            equal = false;
            return true;
        }
        var leftSet = AsSet(left, default);
        var rightSet = AsSet(right, default);
        equal =
            leftSet.Entries.Count == rightSet.Entries.Count
            && PythonSetOperations.IsSubset(leftSet, rightSet, default);
        return true;
    }

    /// <summary>
    /// `isdisjoint(other)`: whether the view holds nothing the other iterable holds. The
    /// other is read through the view, so an unhashable element is refused the way a
    /// dictionary key would be.
    /// </summary>
    internal static PythonValue IsDisjoint(
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"isdisjoint() takes exactly one argument ({arguments.Count} given)",
                span,
                "TypeError"
            );
        var iterator = ManagedObjectProtocols.GetIterator(arguments[0], span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, span))
        {
            if (ManagedObjectProtocols.Contains(receiver, element, span))
                return PythonTruthValue.False;
        }
        return PythonTruthValue.True;
    }
}
