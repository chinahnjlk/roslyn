﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#if DEBUG
// See comment in DataFlowPass.
#define REFERENCE_STATE
#endif

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Nullability flow analysis.
    /// </summary>
    internal sealed partial class NullableWalker : DataFlowPassBase<NullableWalker.LocalState>
    {
        /// <summary>
        /// The inferred type at the point of declaration of var locals.
        /// </summary>
        // PROTOTYPE(NullableReferenceTypes): Does this need to
        // move to LocalState so it participates in merging?
        private readonly PooledDictionary<LocalSymbol, TypeSymbolWithAnnotations> _variableTypes = PooledDictionary<LocalSymbol, TypeSymbolWithAnnotations>.GetInstance();

        /// <summary>
        /// The current source assembly.
        /// </summary>
        private readonly SourceAssemblySymbol _sourceAssembly;

        // PROTOTYPE(NullableReferenceTypes): Remove the Binder if possible. 
        private readonly Binder _binder;

        private readonly Conversions _conversions;

        private readonly Action<BoundExpression, TypeSymbolWithAnnotations> _callbackOpt;

        /// <summary>
        /// Invalid type, used only to catch Visit methods that do not set
        /// _result.Type. See VisitExpressionWithoutStackGuard.
        /// </summary>
        private static readonly TypeSymbolWithAnnotations _invalidType = TypeSymbolWithAnnotations.Create(ErrorTypeSymbol.UnknownResultType);

        private Result _result; // PROTOTYPE(NullableReferenceTypes): Should be return value from the visitor, not mutable state.

        /// <summary>
        /// Reflects the enclosing method or lambda at the current location (in the bound tree).
        /// </summary>
        private MethodSymbol _currentMethodOrLambda;

        /// <summary>
        /// Instances being constructed.
        /// </summary>
        private PooledDictionary<BoundExpression, ObjectCreationPlaceholderLocal> _placeholderLocals;

        /// <summary>
        /// For methods with annotations, we'll need to visit the arguments twice.
        /// Once for diagnostics and once for result state (but disabling diagnostics).
        /// </summary>
        private bool _disableDiagnostics = false;

        protected override void Free()
        {
            _variableTypes.Free();
            _placeholderLocals?.Free();
            base.Free();
        }

        private NullableWalker(
            CSharpCompilation compilation,
            MethodSymbol member,
            BoundNode node,
            Action<BoundExpression, TypeSymbolWithAnnotations> callbackOpt)
            : base(compilation, member, node, new EmptyStructTypeCache(compilation, dev12CompilerCompatibility: false), trackUnassignments: false)
        {
            _sourceAssembly = ((object)member == null) ? null : (SourceAssemblySymbol)member.ContainingAssembly;
            this._currentMethodOrLambda = member;
            _callbackOpt = callbackOpt;
            // PROTOTYPE(NullableReferenceTypes): Do we really need a Binder?
            // If so, are we interested in an InMethodBinder specifically?
            _binder = compilation.GetBinderFactory(node.SyntaxTree).GetBinder(node.Syntax);
            Debug.Assert(!_binder.Conversions.IncludeNullability);
            _conversions = _binder.Conversions.WithNullability(true);
        }

        protected override bool ConvertInsufficientExecutionStackExceptionToCancelledByStackGuardException()
        {
            return true;
        }

        protected override ImmutableArray<PendingBranch> Scan(ref bool badRegion)
        {
            this.Diagnostics.Clear();
            ImmutableArray<ParameterSymbol> methodParameters = MethodParameters;
            ParameterSymbol methodThisParameter = MethodThisParameter;
            this.State = ReachableState();                   // entry point is reachable
            this.regionPlace = RegionPlace.Before;
            EnterParameters(methodParameters);               // with parameters assigned
            if ((object)methodThisParameter != null)
            {
                EnterParameter(methodThisParameter);
            }

            ImmutableArray<PendingBranch> pendingReturns = base.Scan(ref badRegion);
            return pendingReturns;
        }

        public static void Analyze(CSharpCompilation compilation, MethodSymbol member, BoundNode node, DiagnosticBag diagnostics, Action<BoundExpression, TypeSymbolWithAnnotations> callbackOpt = null)
        {
            Debug.Assert(diagnostics != null);

            if (member.IsImplicitlyDeclared)
            {
                return;
            }

            var walker = new NullableWalker(compilation, member, node, callbackOpt);
            try
            {
                bool badRegion = false;
                ImmutableArray<PendingBranch> returns = walker.Analyze(ref badRegion);
                diagnostics.AddRange(walker.Diagnostics);
                Debug.Assert(!badRegion);
            }
            catch (BoundTreeVisitor.CancelledByStackGuardException ex) when (diagnostics != null)
            {
                ex.AddAnError(diagnostics);
            }
            finally
            {
                walker.Free();
            }
        }

        protected override void Normalize(ref LocalState state)
        {
            int oldNext = state.Capacity;
            state.EnsureCapacity(nextVariableSlot);
            Populate(ref state, oldNext);
        }

        private void Populate(ref LocalState state, int start)
        {
            int capacity = state.Capacity;
            for (int slot = start; slot < capacity; slot++)
            {
                var value = GetDefaultState(ref state, slot);
                state[slot] = value;
            }
        }

        private bool? GetDefaultState(ref LocalState state, int slot)
        {
            if (slot == 0)
            {
                return null;
            }

            var variable = variableBySlot[slot];
            var symbol = variable.Symbol;

            switch (symbol.Kind)
            {
                case SymbolKind.Local:
                    return null;
                case SymbolKind.Parameter:
                    {
                        var parameter = (ParameterSymbol)symbol;
                        return (parameter.RefKind == RefKind.Out) ?
                            null :
                            !parameter.Type.IsNullable;
                    }
                case SymbolKind.Field:
                case SymbolKind.Property:
                case SymbolKind.Event:
                    {
                        // PROTOTYPE(NullableReferenceTypes): State of containing struct should not be important.
                        int containingSlot = variable.ContainingSlot;
                        if (containingSlot > 0 &&
                            variableBySlot[containingSlot].Symbol.GetTypeOrReturnType().TypeKind == TypeKind.Struct &&
                            state[containingSlot] == null)
                        {
                            return null;
                        }
                        return !symbol.GetTypeOrReturnType().IsNullable;
                    }
                default:
                    throw ExceptionUtilities.UnexpectedValue(symbol.Kind);
            }
        }

        protected override bool TryGetReceiverAndMember(BoundExpression expr, out BoundExpression receiver, out Symbol member)
        {
            receiver = null;
            member = null;

            switch (expr.Kind)
            {
                case BoundKind.FieldAccess:
                    {
                        var fieldAccess = (BoundFieldAccess)expr;
                        var fieldSymbol = fieldAccess.FieldSymbol;
                        if (fieldSymbol.IsStatic || fieldSymbol.IsFixed)
                        {
                            return false;
                        }
                        member = fieldSymbol;
                        receiver = fieldAccess.ReceiverOpt;
                        break;
                    }
                case BoundKind.EventAccess:
                    {
                        var eventAccess = (BoundEventAccess)expr;
                        var eventSymbol = eventAccess.EventSymbol;
                        if (eventSymbol.IsStatic)
                        {
                            return false;
                        }
                        // PROTOTYPE(NullableReferenceTypes): Use AssociatedField for field-like events?
                        member = eventSymbol;
                        receiver = eventAccess.ReceiverOpt;
                        break;
                    }
                case BoundKind.PropertyAccess:
                    {
                        var propAccess = (BoundPropertyAccess)expr;
                        var propSymbol = propAccess.PropertySymbol;
                        if (propSymbol.IsStatic)
                        {
                            return false;
                        }
                        member = GetBackingFieldIfStructProperty(propSymbol);
                        receiver = propAccess.ReceiverOpt;
                        break;
                    }
            }

            return (object)member != null &&
                (object)receiver != null &&
                receiver.Kind != BoundKind.TypeExpression &&
                (object)receiver.Type != null;
        }

        // PROTOTYPE(NullableReferenceTypes): Use backing field for struct property
        // for now, to avoid cycles if the struct type contains a property of the struct type.
        // Remove this and populate struct members lazily to match classes.
        private Symbol GetBackingFieldIfStructProperty(Symbol symbol)
        {
            if (symbol.Kind == SymbolKind.Property)
            {
                var property = (PropertySymbol)symbol;
                var containingType = property.ContainingType;
                if (containingType.TypeKind == TypeKind.Struct)
                {
                    // PROTOTYPE(NullableReferenceTypes): Relying on field name
                    // will not work for properties declared in other languages.
                    var fieldName = GeneratedNames.MakeBackingFieldName(property.Name);
                    return _emptyStructTypeCache.GetStructInstanceFields(containingType).FirstOrDefault(f => f.Name == fieldName);
                }
            }
            return symbol;
        }

        // PROTOTYPE(NullableReferenceTypes): Temporary, until we're using
        // properties on structs directly.
        private new int GetOrCreateSlot(Symbol symbol, int containingSlot = 0)
        {
            symbol = GetBackingFieldIfStructProperty(symbol);
            if ((object)symbol == null)
            {
                return -1;
            }
            return base.GetOrCreateSlot(symbol, containingSlot);
        }

        // PROTOTYPE(NullableReferenceTypes): Remove use of MakeSlot.
        protected override int MakeSlot(BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundKind.ObjectCreationExpression:
                case BoundKind.AnonymousObjectCreationExpression:
                    if (_placeholderLocals != null && _placeholderLocals.TryGetValue(node, out ObjectCreationPlaceholderLocal placeholder))
                    {
                        return GetOrCreateSlot(placeholder);
                    }
                    break;
            }
            return base.MakeSlot(node);
        }

        private new void VisitLvalue(BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundKind.Local:
                    _result = GetDeclaredLocalResult(((BoundLocal)node).LocalSymbol);
                    break;
                case BoundKind.Parameter:
                    _result = GetDeclaredParameterResult(((BoundParameter)node).ParameterSymbol);
                    break;
                case BoundKind.FieldAccess:
                    {
                        var fieldAccess = (BoundFieldAccess)node;
                        VisitMemberAccess(fieldAccess.ReceiverOpt, fieldAccess.FieldSymbol, asLvalue: true);
                    }
                    break;
                case BoundKind.PropertyAccess:
                    {
                        var propertyAccess = (BoundPropertyAccess)node;
                        VisitMemberAccess(propertyAccess.ReceiverOpt, propertyAccess.PropertySymbol, asLvalue: true);
                    }
                    break;
                case BoundKind.EventAccess:
                    {
                        var eventAccess = (BoundEventAccess)node;
                        VisitMemberAccess(eventAccess.ReceiverOpt, eventAccess.EventSymbol, asLvalue: true);
                    }
                    break;
                case BoundKind.ObjectInitializerMember:
                    throw ExceptionUtilities.UnexpectedValue(node.Kind); // Should have been handled in VisitObjectCreationExpression().
                default:
                    VisitRvalue(node);
                    break;
            }
        }

        private Result VisitRvalueWithResult(BoundExpression node)
        {
            base.VisitRvalue(node);
            return _result;
        }

        private static object GetTypeAsDiagnosticArgument(TypeSymbol typeOpt)
        {
            // PROTOTYPE(NullableReferenceTypes): Avoid hardcoded string.
            return typeOpt ?? (object)"<null>";
        }

        private bool ReportNullReferenceAssignmentIfNecessary(BoundExpression value, TypeSymbolWithAnnotations targetType, TypeSymbolWithAnnotations valueType, bool useLegacyWarnings)
        {
            Debug.Assert(value != null);
            Debug.Assert(!IsConditionalState);

            if (targetType is null || valueType is null)
            {
                return false;
            }

            if (targetType.IsReferenceType && targetType.IsNullable == false && valueType.IsNullable == true)
            {
                if (useLegacyWarnings)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_ConvertingNullableToNonNullable, value.Syntax);
                }
                else if (!ReportNullAsNonNullableReferenceIfNecessary(value))
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullReferenceAssignment, value.Syntax);
                }
                return true;
            }

            return false;
        }

        private void ReportAssignmentWarnings(BoundExpression value, TypeSymbolWithAnnotations targetType, TypeSymbolWithAnnotations valueType, bool useLegacyWarnings)
        {
            Debug.Assert(value != null);
            Debug.Assert(!IsConditionalState);

            if (this.State.Reachable)
            {
                if (targetType is null || valueType is null)
                {
                    return;
                }

                ReportNullReferenceAssignmentIfNecessary(value, targetType, valueType, useLegacyWarnings);
                ReportNullabilityMismatchInAssignmentIfNecessary(value, valueType.TypeSymbol, targetType.TypeSymbol);
            }
        }

        /// <summary>
        /// Update tracked value on assignment.
        /// </summary>
        private void TrackNullableStateForAssignment(BoundExpression value, TypeSymbolWithAnnotations targetType, int targetSlot, TypeSymbolWithAnnotations valueType, int valueSlot = -1)
        {
            Debug.Assert(value != null);
            Debug.Assert(!IsConditionalState);

            if (this.State.Reachable)
            {
                if ((object)targetType == null)
                {
                    return;
                }

                if (targetSlot <= 0)
                {
                    return;
                }

                bool isByRefTarget = IsByRefTarget(targetSlot);
                if (targetSlot >= this.State.Capacity) Normalize(ref this.State);

                this.State[targetSlot] = isByRefTarget ?
                    // Since reference can point to the heap, we cannot assume the value is not null after this assignment,
                    // regardless of what value is being assigned. 
                    (targetType.IsNullable == true) ? (bool?)false : null :
                    !valueType?.IsNullable;

                // PROTOTYPE(NullableReferenceTypes): Might this clear state that
                // should be copied in InheritNullableStateOfTrackableType?
                InheritDefaultState(targetSlot);

                if (targetType.IsReferenceType)
                {
                    // PROTOTYPE(NullableReferenceTypes): We should copy all tracked state from `value`,
                    // regardless of BoundNode type, but we'll need to handle cycles. (For instance, the
                    // assignment to C.F below. See also StaticNullChecking_Members.FieldCycle_01.)
                    // class C
                    // {
                    //     C? F;
                    //     C() { F = this; }
                    // }
                    // For now, we copy a limited set of BoundNode types that shouldn't contain cycles.
                    if ((value.Kind == BoundKind.ObjectCreationExpression || value.Kind == BoundKind.AnonymousObjectCreationExpression || value.Kind == BoundKind.DynamicObjectCreationExpression || targetType.TypeSymbol.IsAnonymousType) &&
                        targetType.TypeSymbol.Equals(valueType?.TypeSymbol, TypeCompareKind.ConsiderEverything)) // PROTOTYPE(NullableReferenceTypes): Allow assignment to base type.
                    {
                        if (valueSlot > 0)
                        {
                            InheritNullableStateOfTrackableType(targetSlot, valueSlot, isByRefTarget, slotWatermark: GetSlotWatermark());
                        }
                    }
                }
                else if (EmptyStructTypeCache.IsTrackableStructType(targetType.TypeSymbol) &&
                        targetType.TypeSymbol.Equals(valueType?.TypeSymbol, TypeCompareKind.ConsiderEverything))
                {
                    InheritNullableStateOfTrackableStruct(targetType.TypeSymbol, targetSlot, valueSlot, IsByRefTarget(targetSlot), slotWatermark: GetSlotWatermark());
                }
            }
        }

        private int GetSlotWatermark() => this.nextVariableSlot;

        private bool IsByRefTarget(int slot)
        {
            if (slot > 0)
            {
                Symbol associatedNonMemberSymbol = GetNonMemberSymbol(slot);

                switch (associatedNonMemberSymbol.Kind)
                {
                    case SymbolKind.Local:
                        return ((LocalSymbol)associatedNonMemberSymbol).RefKind != RefKind.None;
                    case SymbolKind.Parameter:
                        var parameter = (ParameterSymbol)associatedNonMemberSymbol;
                        return !parameter.IsThis && parameter.RefKind != RefKind.None;
                }
            }

            return false;
        }

        private void ReportStaticNullCheckingDiagnostics(ErrorCode errorCode, SyntaxNode syntaxNode, params object[] arguments)
        {
            if (!_disableDiagnostics)
            {
                Diagnostics.Add(errorCode, syntaxNode.GetLocation(), arguments);
            }
        }

        private void InheritNullableStateOfTrackableStruct(TypeSymbol targetType, int targetSlot, int valueSlot, bool isByRefTarget, int slotWatermark)
        {
            Debug.Assert(targetSlot > 0);
            Debug.Assert(EmptyStructTypeCache.IsTrackableStructType(targetType));

            // PROTOTYPE(NullableReferenceTypes): Handle properties not backed by fields.
            // See ModifyMembers_StructPropertyNoBackingField and PropertyCycle_Struct tests.
            foreach (var field in _emptyStructTypeCache.GetStructInstanceFields(targetType))
            {
                InheritNullableStateOfFieldOrProperty(targetSlot, valueSlot, field, isByRefTarget, slotWatermark);
            }
        }

        // 'slotWatermark' is used to avoid inheriting members from inherited members.
        private void InheritNullableStateOfFieldOrProperty(int targetContainerSlot, int valueContainerSlot, Symbol fieldOrProperty, bool isByRefTarget, int slotWatermark)
        {
            Debug.Assert(valueContainerSlot <= slotWatermark);

            TypeSymbolWithAnnotations fieldOrPropertyType = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(fieldOrProperty);

            if (fieldOrPropertyType.IsReferenceType)
            {
                // If statically declared as not-nullable, no need to adjust the tracking info. 
                // Declaration information takes priority.
                if (fieldOrPropertyType.IsNullable != false)
                {
                    int targetMemberSlot = GetOrCreateSlot(fieldOrProperty, targetContainerSlot);
                    bool? value = !fieldOrPropertyType.IsNullable;
                    if (isByRefTarget)
                    {
                        // This is a property/field access through a by ref entity and it isn't considered declared as not-nullable. 
                        // Since reference can point to the heap, we cannot assume the property/field doesn't have null value after this assignment,
                        // regardless of what value is being assigned.
                    }
                    else if (valueContainerSlot > 0)
                    {
                        int valueMemberSlot = VariableSlot(fieldOrProperty, valueContainerSlot);
                        value = valueMemberSlot > 0 && valueMemberSlot < this.State.Capacity ?
                            this.State[valueMemberSlot] :
                            null;
                    }

                    this.State[targetMemberSlot] = value;
                }

                if (valueContainerSlot > 0)
                {
                    int valueMemberSlot = VariableSlot(fieldOrProperty, valueContainerSlot);
                    if (valueMemberSlot > 0 && valueMemberSlot <= slotWatermark)
                    {
                        int targetMemberSlot = GetOrCreateSlot(fieldOrProperty, targetContainerSlot);
                        InheritNullableStateOfTrackableType(targetMemberSlot, valueMemberSlot, isByRefTarget, slotWatermark);
                    }
                }
            }
            else if (EmptyStructTypeCache.IsTrackableStructType(fieldOrPropertyType.TypeSymbol))
            {
                int targetMemberSlot = GetOrCreateSlot(fieldOrProperty, targetContainerSlot);
                if (targetMemberSlot > 0)
                {
                    int valueMemberSlot = -1;
                    if (valueContainerSlot > 0)
                    {
                        int slot = GetOrCreateSlot(fieldOrProperty, valueContainerSlot);
                        if (slot < slotWatermark)
                        {
                            valueMemberSlot = slot;
                        }
                    }
                    InheritNullableStateOfTrackableStruct(fieldOrPropertyType.TypeSymbol, targetMemberSlot, valueMemberSlot, isByRefTarget, slotWatermark);
                }
            }
        }

        private void InheritDefaultState(int targetSlot)
        {
            Debug.Assert(targetSlot > 0);

            // Reset the state of any members of the target.
            for (int slot = targetSlot + 1; slot < nextVariableSlot; slot++)
            {
                var variable = variableBySlot[slot];
                if (variable.ContainingSlot != targetSlot)
                {
                    continue;
                }
                this.State[slot] = !variable.Symbol.GetTypeOrReturnType().IsNullable;
                InheritDefaultState(slot);
            }
        }

        private void InheritNullableStateOfTrackableType(int targetSlot, int valueSlot, bool isByRefTarget, int slotWatermark)
        {
            Debug.Assert(targetSlot > 0);
            Debug.Assert(valueSlot > 0);

            // Clone the state for members that have been set on the value.
            for (int slot = valueSlot + 1; slot < nextVariableSlot; slot++)
            {
                var variable = variableBySlot[slot];
                if (variable.ContainingSlot != valueSlot)
                {
                    continue;
                }
                var member = variable.Symbol;
                Debug.Assert(member.Kind == SymbolKind.Field || member.Kind == SymbolKind.Property);
                InheritNullableStateOfFieldOrProperty(targetSlot, valueSlot, member, isByRefTarget, slotWatermark);
            }
        }

        protected override LocalState ReachableState()
        {
            var state = new LocalState(BitVector.Create(nextVariableSlot), BitVector.Create(nextVariableSlot));
            Populate(ref state, start: 0);
            return state;
        }

        protected override LocalState UnreachableState()
        {
            return new LocalState(BitVector.Empty, BitVector.Empty);
        }

        protected override LocalState AllBitsSet()
        {
            return new LocalState(BitVector.Create(nextVariableSlot), BitVector.Create(nextVariableSlot));
        }

        private void EnterParameters(ImmutableArray<ParameterSymbol> parameters)
        {
            // label out parameters as not assigned.
            foreach (var parameter in parameters)
            {
                EnterParameter(parameter);
            }
        }

        private void EnterParameter(ParameterSymbol parameter)
        {
            int slot = GetOrCreateSlot(parameter);
            Debug.Assert(!IsConditionalState);
            if (slot > 0 && parameter.RefKind != RefKind.Out)
            {
                var paramType = parameter.Type.TypeSymbol;
                if (EmptyStructTypeCache.IsTrackableStructType(paramType))
                {
                    InheritNullableStateOfTrackableStruct(paramType, slot, valueSlot: -1, isByRefTarget: parameter.RefKind != RefKind.None, slotWatermark: GetSlotWatermark());
                }
            }
        }

        #region Visitors

        public override BoundNode VisitIsPatternExpression(BoundIsPatternExpression node)
        {
            // PROTOTYPE(NullableReferenceTypes): Move these asserts to base class.
            Debug.Assert(!IsConditionalState);

            // Create slot when the state is unconditional since EnsureCapacity should be
            // called on all fields and that is simpler if state is limited to this.State.
            int slot = -1;
            if (this.State.Reachable)
            {
                var pattern = node.Pattern;
                // PROTOTYPE(NullableReferenceTypes): Handle patterns that ensure x is not null:
                // x is T y // where T is not inferred via var
                // x is K // where K is a constant other than null
                if (pattern.Kind == BoundKind.ConstantPattern && ((BoundConstantPattern)pattern).ConstantValue?.IsNull == true)
                {
                    slot = MakeSlot(node.Expression);
                    if (slot > 0)
                    {
                        Normalize(ref this.State);
                    }
                }
            }

            var result = base.VisitIsPatternExpression(node);

            Debug.Assert(IsConditionalState);
            if (slot > 0)
            {
                this.StateWhenTrue[slot] = false;
                this.StateWhenFalse[slot] = true;
            }

            SetResult(node);
            return result;
        }

        public override void VisitPattern(BoundExpression expression, BoundPattern pattern)
        {
            base.VisitPattern(expression, pattern);
            var whenFail = StateWhenFalse;
            SetState(StateWhenTrue);
            AssignPatternVariables(pattern);
            SetConditionalState(this.State, whenFail);
            SetUnknownResultNullability();
        }

        private void AssignPatternVariables(BoundPattern pattern)
        {
            switch (pattern.Kind)
            {
                case BoundKind.DeclarationPattern:
                    // PROTOTYPE(NullableReferenceTypes): Handle.
                    break;
                case BoundKind.WildcardPattern:
                    break;
                case BoundKind.ConstantPattern:
                    {
                        var pat = (BoundConstantPattern)pattern;
                        this.VisitRvalue(pat.Value);
                        break;
                    }
                default:
                    break;
            }
        }

        protected override BoundNode VisitReturnStatementNoAdjust(BoundReturnStatement node)
        {
            Debug.Assert(!IsConditionalState);

            BoundExpression expr = node.ExpressionOpt;
            if (expr == null)
            {
                return null;
            }

            Conversion conversion;
            (expr, conversion) = RemoveConversion(expr, includeExplicitConversions: false);
            Result result = VisitRvalueWithResult(expr);

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            if (expr.Type?.IsErrorType() == true)
            {
                return null;
            }

            TypeSymbolWithAnnotations returnType = GetReturnType(compilation, _currentMethodOrLambda);
            if ((object)returnType != null)
            {
                TypeSymbolWithAnnotations resultType = ApplyConversion(expr, expr, conversion, returnType.TypeSymbol, result.Type, checkConversion: true, fromExplicitCast: false, out bool canConvertNestedNullability);
                if (!canConvertNestedNullability)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, expr.Syntax, GetTypeAsDiagnosticArgument(result.Type?.TypeSymbol), returnType.TypeSymbol);
                }

                bool returnTypeIsNonNullable = IsNonNullable(returnType);
                bool returnTypeIsUnconstrainedTypeParameter = IsUnconstrainedTypeParameter(returnType.TypeSymbol);
                bool reportedNullable = false;
                if (returnTypeIsNonNullable || returnTypeIsUnconstrainedTypeParameter)
                {
                    reportedNullable = ReportNullAsNonNullableReferenceIfNecessary(node.ExpressionOpt);
                }

                if (!reportedNullable)
                {
                    if (IsNullable(resultType) && (returnTypeIsNonNullable || returnTypeIsUnconstrainedTypeParameter) ||
                        IsUnconstrainedTypeParameter(resultType?.TypeSymbol) && returnTypeIsNonNullable)
                    {
                        ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullReferenceReturn, node.ExpressionOpt.Syntax);
                    }
                }
            }

            return null;
        }

        private static TypeSymbolWithAnnotations GetReturnType(CSharpCompilation compilation, MethodSymbol method)
        {
            var returnType = method.ReturnType;
            if (returnType is null)
            {
                return null;
            }
            return method.IsGenericTaskReturningAsync(compilation) ?
                ((NamedTypeSymbol)returnType.TypeSymbol).TypeArgumentsNoUseSiteDiagnostics.Single() :
                returnType;
        }

        private static bool IsNullable(TypeSymbolWithAnnotations typeOpt)
        {
            return typeOpt?.IsNullable == true;
        }

        private static bool IsNonNullable(TypeSymbolWithAnnotations typeOpt)
        {
            return typeOpt?.IsNullable == false && typeOpt.IsReferenceType;
        }

        private static bool IsUnconstrainedTypeParameter(TypeSymbol typeOpt)
        {
            return typeOpt?.IsUnconstrainedTypeParameter() == true;
        }

        /// <summary>
        /// Report warning assigning value where nested nullability does not match
        /// target (e.g.: `object[] a = new[] { maybeNull }`).
        /// </summary>
        private void ReportNullabilityMismatchInAssignmentIfNecessary(BoundExpression node, TypeSymbol sourceType, TypeSymbol destinationType)
        {
            if ((object)sourceType != null && IsNullabilityMismatch(destinationType, sourceType))
            {
                ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, node.Syntax, sourceType, destinationType);
            }
        }

        public override BoundNode VisitLocal(BoundLocal node)
        {
            _result = GetAdjustedResult(GetDeclaredLocalResult(node.LocalSymbol));
            return null;
        }

        public override BoundNode VisitLocalDeclaration(BoundLocalDeclaration node)
        {
            var local = node.LocalSymbol;
            int slot = GetOrCreateSlot(local);

            var initializer = node.InitializerOpt;
            if (initializer is null)
            {
                return null;
            }

            Conversion conversion;
            (initializer, conversion) = RemoveConversion(initializer, includeExplicitConversions: false);

            Result value = VisitRvalueWithResult(initializer);
            TypeSymbolWithAnnotations type = local.Type;
            TypeSymbolWithAnnotations valueType = value.Type;

            if (node.DeclaredType.InferredType)
            {
                Debug.Assert(conversion.IsIdentity);
                if (valueType is null)
                {
                    Debug.Assert(type.IsErrorType());
                    valueType = type;
                }
                _variableTypes[local] = valueType;
                type = valueType;
            }
            else
            {
                var unconvertedType = valueType;
                valueType = ApplyConversion(initializer, initializer, conversion, type.TypeSymbol, valueType, checkConversion: true, fromExplicitCast: false, out bool canConvertNestedNullability);
                // Need to report all warnings that apply since the warnings can be suppressed individually.
                ReportNullReferenceAssignmentIfNecessary(initializer, type, valueType, useLegacyWarnings: true);
                if (!canConvertNestedNullability)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, initializer.Syntax, GetTypeAsDiagnosticArgument(unconvertedType?.TypeSymbol), type.TypeSymbol);
                }
            }

            TrackNullableStateForAssignment(initializer, type, slot, valueType, value.Slot);
            return null;
        }

        protected override BoundExpression VisitExpressionWithoutStackGuard(BoundExpression node)
        {
            Debug.Assert(!IsConditionalState);
            _result = _invalidType; // PROTOTYPE(NullableReferenceTypes): Move to `Visit` method?
            var result = base.VisitExpressionWithoutStackGuard(node);
#if DEBUG
            // Verify Visit method set _result.
            TypeSymbolWithAnnotations resultType = _result.Type;
            Debug.Assert((object)resultType != _invalidType);
            Debug.Assert(AreCloseEnough(resultType?.TypeSymbol, node.Type));
#endif
            if (_callbackOpt != null)
            {
                _callbackOpt(node, _result.Type);
            }
            return result;
        }

#if DEBUG
        // For asserts only.
        private static bool AreCloseEnough(TypeSymbol typeA, TypeSymbol typeB)
        {
            if ((object)typeA == typeB)
            {
                return true;
            }
            if (typeA is null || typeB is null)
            {
                return false;
            }
            bool canIgnoreType(TypeSymbol type) => (object)type.VisitType((t, unused1, unused2) => t.IsErrorType() || t.IsDynamic() || t.HasUseSiteError, (object)null) != null;
            return canIgnoreType(typeA) ||
                canIgnoreType(typeB) ||
                typeA.Equals(typeB, TypeCompareKind.IgnoreCustomModifiersAndArraySizesAndLowerBounds | TypeCompareKind.IgnoreDynamicAndTupleNames); // Ignore TupleElementNames (see https://github.com/dotnet/roslyn/issues/23651).
        }
#endif

        protected override void VisitStatement(BoundStatement statement)
        {
            _result = _invalidType;
            base.VisitStatement(statement);
            _result = _invalidType;
        }

        public override BoundNode VisitObjectCreationExpression(BoundObjectCreationExpression node)
        {
            Debug.Assert(!IsConditionalState);
            VisitArguments(node, node.Arguments, node.ArgumentRefKindsOpt, node.Constructor, node.ArgsToParamsOpt, node.Expanded);
            VisitObjectOrDynamicObjectCreation(node, node.InitializerExpressionOpt);
            return null;
        }

        private void VisitObjectOrDynamicObjectCreation(BoundExpression node, BoundExpression initializerOpt)
        {
            Debug.Assert(node.Kind == BoundKind.ObjectCreationExpression || node.Kind == BoundKind.DynamicObjectCreationExpression);

            LocalSymbol receiver = null;
            int slot = -1;
            TypeSymbol type = node.Type;
            if ((object)type != null)
            {
                bool isTrackableStructType = EmptyStructTypeCache.IsTrackableStructType(type);
                if (type.IsReferenceType || isTrackableStructType)
                {
                    receiver = GetOrCreateObjectCreationPlaceholder(node);
                    slot = GetOrCreateSlot(receiver);
                    if (slot > 0 && isTrackableStructType)
                    {
                        this.State[slot] = true;
                        InheritNullableStateOfTrackableStruct(type, slot, valueSlot: -1, isByRefTarget: false, slotWatermark: GetSlotWatermark());
                    }
                }
            }

            if (initializerOpt != null)
            {
                VisitObjectCreationInitializer(receiver, slot, initializerOpt);
            }

            _result = Result.Create(TypeSymbolWithAnnotations.Create(type), slot);
        }

        private void VisitObjectCreationInitializer(Symbol containingSymbol, int containingSlot, BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundKind.ObjectInitializerExpression:
                    foreach (var initializer in ((BoundObjectInitializerExpression)node).Initializers)
                    {
                        switch (initializer.Kind)
                        {
                            case BoundKind.AssignmentOperator:
                                VisitObjectElementInitializer(containingSymbol, containingSlot, (BoundAssignmentOperator)initializer);
                                break;
                            default:
                                VisitRvalue(initializer);
                                break;
                        }
                    }
                    break;
                case BoundKind.CollectionInitializerExpression:
                    foreach (var initializer in ((BoundCollectionInitializerExpression)node).Initializers)
                    {
                        switch (initializer.Kind)
                        {
                            case BoundKind.CollectionElementInitializer:
                                VisitCollectionElementInitializer((BoundCollectionElementInitializer)initializer);
                                break;
                            default:
                                VisitRvalue(initializer);
                                break;
                        }
                    }
                    break;
                default:
                    Result result = VisitRvalueWithResult(node);
                    if ((object)containingSymbol != null)
                    {
                        var type = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(containingSymbol);
                        ReportAssignmentWarnings(node, type, result.Type, useLegacyWarnings: false);
                        TrackNullableStateForAssignment(node, type, containingSlot, result.Type, result.Slot);
                    }
                    break;
            }
        }

        private void VisitObjectElementInitializer(Symbol containingSymbol, int containingSlot, BoundAssignmentOperator node)
        {
            var left = node.Left;
            switch (left.Kind)
            {
                case BoundKind.ObjectInitializerMember:
                    {
                        var objectInitializer = (BoundObjectInitializerMember)left;
                        var symbol = objectInitializer.MemberSymbol;
                        if (!objectInitializer.Arguments.IsDefaultOrEmpty)
                        {
                            VisitArguments(objectInitializer, objectInitializer.Arguments, objectInitializer.ArgumentRefKindsOpt, (PropertySymbol)symbol, objectInitializer.ArgsToParamsOpt, objectInitializer.Expanded);
                        }
                        if ((object)symbol != null)
                        {
                            int slot = (containingSlot < 0) ? -1 : GetOrCreateSlot(symbol, containingSlot);
                            VisitObjectCreationInitializer(symbol, slot, node.Right);
                        }
                    }
                    break;
                default:
                    VisitLvalue(node);
                    break;
            }
        }

        private new void VisitCollectionElementInitializer(BoundCollectionElementInitializer node)
        {
            if (node.AddMethod.CallsAreOmitted(node.SyntaxTree))
            {
                // PROTOTYPE(NullableReferenceTypes): Should skip state set in arguments
                // of omitted call. See PreciseAbstractFlowPass.VisitCollectionElementInitializer.
            }

            VisitArguments(node, node.Arguments, default(ImmutableArray<RefKind>), node.AddMethod, node.ArgsToParamsOpt, node.Expanded);
            SetUnknownResultNullability();
        }

        private void SetResult(BoundExpression node)
        {
            _result = TypeSymbolWithAnnotations.Create(node.Type);
        }

        private ObjectCreationPlaceholderLocal GetOrCreateObjectCreationPlaceholder(BoundExpression node)
        {
            ObjectCreationPlaceholderLocal placeholder;
            if (_placeholderLocals == null)
            {
                _placeholderLocals = PooledDictionary<BoundExpression, ObjectCreationPlaceholderLocal>.GetInstance();
                placeholder = null;
            }
            else
            {
                _placeholderLocals.TryGetValue(node, out placeholder);
            }

            if ((object)placeholder == null)
            {
                placeholder = new ObjectCreationPlaceholderLocal(_member, node);
                _placeholderLocals.Add(node, placeholder);
            }

            return placeholder;
        }

        public override BoundNode VisitAnonymousObjectCreationExpression(BoundAnonymousObjectCreationExpression node)
        {
            Debug.Assert(!IsConditionalState);

            int receiverSlot = -1;
            var arguments = node.Arguments;
            var constructor = node.Constructor;
            for (int i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i];
                Result argumentResult = VisitRvalueWithResult(argument);
                var parameter = constructor.Parameters[i];
                ReportArgumentWarnings(argument, argumentResult.Type, parameter);

                // PROTOTYPE(NullableReferenceTypes): node.Declarations includes
                // explicitly-named properties only. For now, skip expressions
                // with implicit names. See StaticNullChecking.AnonymousTypes_05.
                if (node.Declarations.Length < arguments.Length)
                {
                    continue;
                }

                PropertySymbol property = node.Declarations[i].Property;
                if (receiverSlot <= 0)
                {
                    ObjectCreationPlaceholderLocal implicitReceiver = GetOrCreateObjectCreationPlaceholder(node);
                    receiverSlot = GetOrCreateSlot(implicitReceiver);
                }

                ReportAssignmentWarnings(argument, property.Type, argumentResult.Type, useLegacyWarnings: false);
                TrackNullableStateForAssignment(argument, property.Type, GetOrCreateSlot(property, receiverSlot), argumentResult.Type, argumentResult.Slot);
            }

            // PROTOTYPE(NullableReferenceTypes): Result.Type may need to be a new anonymous
            // type since the properties may have distinct nullability from original.
            // (See StaticNullChecking_FlowAnalysis.AnonymousObjectCreation_02.)
            _result = Result.Create(TypeSymbolWithAnnotations.Create(node.Type), receiverSlot);
            return null;
        }

        public override BoundNode VisitArrayCreation(BoundArrayCreation node)
        {
            foreach (var expr in node.Bounds)
            {
                VisitRvalue(expr);
            }
            TypeSymbol resultType = (node.InitializerOpt == null) ? node.Type : VisitArrayInitializer(node);
            _result = TypeSymbolWithAnnotations.Create(resultType);
            return null;
        }

        private ArrayTypeSymbol VisitArrayInitializer(BoundArrayCreation node)
        {
            var arrayType = (ArrayTypeSymbol)node.Type;
            var elementType = arrayType.ElementType;

            BoundArrayInitialization initialization = node.InitializerOpt;
            var elementBuilder = ArrayBuilder<BoundExpression>.GetInstance(initialization.Initializers.Length);
            GetArrayElements(initialization, elementBuilder);

            // PROTOTYPE(NullableReferenceTypes): Removing and recalculating conversions should not
            // be necessary for explicitly typed arrays. In those cases, VisitConversion should warn
            // on nullability mismatch (although we'll need to ensure we handle the case where
            // initial binding calculated an Identity conversion, even though nullability was distinct).
            int n = elementBuilder.Count;
            var conversionBuilder = ArrayBuilder<Conversion>.GetInstance(n);
            var resultBuilder = ArrayBuilder<Result>.GetInstance(n);
            for (int i = 0; i < n; i++)
            {
                (BoundExpression element, Conversion conversion) = RemoveConversion(elementBuilder[i], includeExplicitConversions: false);
                elementBuilder[i] = element;
                conversionBuilder.Add(conversion);
                resultBuilder.Add(VisitRvalueWithResult(element));
            }

            // PROTOTYPE(NullableReferenceTypes): Record in the BoundArrayCreation
            // whether the array was implicitly typed, rather than relying on syntax.
            if (node.Syntax.Kind() == SyntaxKind.ImplicitArrayCreationExpression)
            {
                var resultTypes = resultBuilder.SelectAsArray(r => r.Type);
                // PROTOTYPE(NullableReferenceTypes): Initial binding calls InferBestType(ImmutableArray<BoundExpression>, ...)
                // overload. Why are we calling InferBestType(ImmutableArray<TypeSymbolWithAnnotations>, ...) here?
                // PROTOTYPE(NullableReferenceTypes): InferBestType(ImmutableArray<BoundExpression>, ...)
                // uses a HashSet<TypeSymbol> to reduce the candidates to the unique types before comparing.
                // Should do the same here.
                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                // If there are error types, use the first error type. (Matches InferBestType(ImmutableArray<BoundExpression>, ...).)
                var bestType = resultTypes.FirstOrDefault(t => t?.IsErrorType() == true) ??
                    BestTypeInferrer.InferBestType(resultTypes, _conversions, useSiteDiagnostics: ref useSiteDiagnostics);
                // PROTOTYPE(NullableReferenceTypes): Report a special ErrorCode.WRN_NoBestNullabilityArrayElements
                // when InferBestType fails, and avoid reporting conversion warnings for each element in those cases.
                // (See similar code for conditional expressions: ErrorCode.WRN_NoBestNullabilityConditionalExpression.)
                if ((object)bestType != null)
                {
                    elementType = bestType;
                }
                arrayType = arrayType.WithElementType(elementType);
            }

            if ((object)elementType != null)
            {
                bool elementTypeIsReferenceType = elementType.IsReferenceType == true;
                for (int i = 0; i < n; i++)
                {
                    var conversion = conversionBuilder[i];
                    var element = elementBuilder[i];
                    var resultType = resultBuilder[i].Type;
                    var sourceType = resultType?.TypeSymbol;
                    if (elementTypeIsReferenceType)
                    {
                        resultType = ApplyConversion(element, element, conversion, elementType.TypeSymbol, resultType, checkConversion: true, fromExplicitCast: false, out bool canConvertNestedNullability);
                        ReportNullReferenceAssignmentIfNecessary(element, elementType, resultType, useLegacyWarnings: false);
                        if (!canConvertNestedNullability)
                        {
                            ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, element.Syntax, sourceType, elementType.TypeSymbol);
                        }
                    }
                }
            }

            resultBuilder.Free();
            elementBuilder.Free();
            _result = _invalidType;
            return arrayType;
        }

        private static void GetArrayElements(BoundArrayInitialization node, ArrayBuilder<BoundExpression> builder)
        {
            foreach (var child in node.Initializers)
            {
                if (child.Kind == BoundKind.ArrayInitialization)
                {
                    GetArrayElements((BoundArrayInitialization)child, builder);
                }
                else
                {
                    builder.Add(child);
                }
            }
        }

        public override BoundNode VisitArrayAccess(BoundArrayAccess node)
        {
            Debug.Assert(!IsConditionalState);

            VisitRvalue(node.Expression);

            Debug.Assert(!IsConditionalState);
            // No need to check expression type since System.Array is a reference type.
            Debug.Assert(node.Expression.Type.IsReferenceType);
            CheckPossibleNullReceiver(node.Expression, checkType: false);

            var type = _result.Type?.TypeSymbol as ArrayTypeSymbol;

            foreach (var i in node.Indices)
            {
                VisitRvalue(i);
            }

            _result = type?.ElementType;
            return null;
        }

        private TypeSymbolWithAnnotations InferResultNullability(BoundBinaryOperator node, TypeSymbolWithAnnotations leftType, TypeSymbolWithAnnotations rightType)
        {
            return InferResultNullability(node.OperatorKind, node.MethodOpt, node.Type, leftType, rightType);
        }

        private TypeSymbolWithAnnotations InferResultNullability(BinaryOperatorKind operatorKind, MethodSymbol methodOpt, TypeSymbol resultType, TypeSymbolWithAnnotations leftType, TypeSymbolWithAnnotations rightType)
        {
            bool? isNullable = null;
            if (operatorKind.IsUserDefined())
            {
                if (operatorKind.IsLifted())
                {
                    // PROTOTYPE(NullableReferenceTypes): Conversions: Lifted operator
                    return TypeSymbolWithAnnotations.Create(resultType, isNullableIfReferenceType: null);
                }
                // PROTOTYPE(NullableReferenceTypes): Update method based on operand types.
                if ((object)methodOpt != null && methodOpt.ParameterCount == 2)
                {
                    return methodOpt.ReturnType;
                }
            }
            else if (!operatorKind.IsDynamic() && resultType.IsReferenceType == true)
            {
                switch (operatorKind.Operator() | operatorKind.OperandTypes())
                {
                    case BinaryOperatorKind.DelegateCombination:
                        {
                            bool? leftIsNullable = leftType?.IsNullable;
                            bool? rightIsNullable = rightType?.IsNullable;
                            if (leftIsNullable == false || rightIsNullable == false)
                            {
                                isNullable = false;
                            }
                            else if (leftIsNullable == true && rightIsNullable == true)
                            {
                                isNullable = true;
                            }
                            else
                            {
                                Debug.Assert(leftIsNullable == null || rightIsNullable == null);
                            }
                        }
                        break;
                    case BinaryOperatorKind.DelegateRemoval:
                        isNullable = true; // Delegate removal can produce null.
                        break;
                    default:
                        isNullable = false;
                        break;
                }
            }
            return TypeSymbolWithAnnotations.Create(resultType, isNullable);
        }

        protected override void AfterLeftChildHasBeenVisited(BoundBinaryOperator binary)
        {
            Debug.Assert(!IsConditionalState);
            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                TypeSymbolWithAnnotations leftType = _result.Type;
                bool warnOnNullReferenceArgument = (binary.OperatorKind.IsUserDefined() && (object)binary.MethodOpt != null && binary.MethodOpt.ParameterCount == 2);

                if (warnOnNullReferenceArgument)
                {
                    ReportArgumentWarnings(binary.Left, leftType, binary.MethodOpt.Parameters[0]);
                }

                VisitRvalue(binary.Right);
                Debug.Assert(!IsConditionalState);

                // At this point, State.Reachable may be false for
                // invalid code such as `s + throw new Exception()`.
                TypeSymbolWithAnnotations rightType = _result.Type;

                if (warnOnNullReferenceArgument)
                {
                    ReportArgumentWarnings(binary.Right, rightType, binary.MethodOpt.Parameters[1]);
                }

                Debug.Assert(!IsConditionalState);
                _result = InferResultNullability(binary, leftType, rightType);

                BinaryOperatorKind op = binary.OperatorKind.Operator();
                if (op == BinaryOperatorKind.Equal || op == BinaryOperatorKind.NotEqual)
                {
                    BoundExpression operandComparedToNull = null;
                    TypeSymbolWithAnnotations operandComparedToNullType = null;

                    if (binary.Right.ConstantValue?.IsNull == true)
                    {
                        operandComparedToNull = binary.Left;
                        operandComparedToNullType = leftType;
                    }
                    else if (binary.Left.ConstantValue?.IsNull == true)
                    {
                        operandComparedToNull = binary.Right;
                        operandComparedToNullType = rightType;
                    }

                    if (operandComparedToNull != null)
                    {
                        // PROTOTYPE(NullableReferenceTypes): This check is incorrect since it compares declared
                        // nullability rather than tracked nullability. Moreover, we should only report such
                        // diagnostics for locals that are set or checked explicitly within this method.
                        if (operandComparedToNullType?.IsNullable == false)
                        {
                            ReportStaticNullCheckingDiagnostics(op == BinaryOperatorKind.Equal ?
                                                                    ErrorCode.HDN_NullCheckIsProbablyAlwaysFalse :
                                                                    ErrorCode.HDN_NullCheckIsProbablyAlwaysTrue,
                                                                binary.Syntax);
                        }

                        // Skip reference conversions
                        operandComparedToNull = SkipReferenceConversions(operandComparedToNull);

                        if (operandComparedToNull.Type?.IsReferenceType == true)
                        {
                            int slot = MakeSlot(operandComparedToNull);

                            if (slot > 0)
                            {
                                if (slot >= this.State.Capacity) Normalize(ref this.State);

                                Split();

                                if (op == BinaryOperatorKind.Equal)
                                {
                                    this.StateWhenFalse[slot] = true;
                                }
                                else
                                {
                                    this.StateWhenTrue[slot] = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static BoundExpression SkipReferenceConversions(BoundExpression possiblyConversion)
        {
            while (possiblyConversion.Kind == BoundKind.Conversion)
            {
                var conversion = (BoundConversion)possiblyConversion;
                switch (conversion.ConversionKind)
                {
                    case ConversionKind.ImplicitReference:
                    case ConversionKind.ExplicitReference:
                        possiblyConversion = conversion.Operand;
                        break;

                    default:
                        return possiblyConversion;
                }
            }

            return possiblyConversion;
        }

        public override BoundNode VisitNullCoalescingOperator(BoundNullCoalescingOperator node)
        {
            Debug.Assert(!IsConditionalState);

            BoundExpression leftOperand = node.LeftOperand;
            BoundExpression rightOperand = node.RightOperand;

            Result leftResult = VisitRvalueWithResult(leftOperand);
            Result rightResult;

            if (IsConstantNull(leftOperand))
            {
                rightResult = VisitRvalueWithResult(rightOperand);
                // Should be able to use rightResult for the result of the operator but
                // binding may have generated a different result type in the case of errors.
                _result = TypeSymbolWithAnnotations.Create(node.Type, getIsNullable(rightOperand, rightResult));
                return null;
            }

            var leftState = this.State.Clone();
            if (leftResult.Type?.IsNullable == false)
            {
                ReportStaticNullCheckingDiagnostics(ErrorCode.HDN_ExpressionIsProbablyNeverNull, leftOperand.Syntax);
            }

            bool leftIsConstant = leftOperand.ConstantValue != null;
            if (leftIsConstant)
            {
                SetUnreachable();
            }

            // PROTOTYPE(NullableReferenceTypes): For cases where the left operand determines
            // the type, we should unwrap the right conversion and re-apply.
            rightResult = VisitRvalueWithResult(rightOperand);
            IntersectWith(ref this.State, ref leftState);
            TypeSymbol resultType;
            var leftResultType = leftResult.Type?.TypeSymbol;
            var rightResultType = rightResult.Type?.TypeSymbol;
            switch (node.OperatorResultKind)
            {
                case BoundNullCoalescingOperatorResultKind.NoCommonType:
                    resultType = node.Type;
                    break;
                case BoundNullCoalescingOperatorResultKind.LeftType:
                    resultType = getLeftResultType(leftResultType, rightResultType);
                    break;
                case BoundNullCoalescingOperatorResultKind.LeftUnwrappedType:
                    resultType = getLeftResultType(leftResultType.StrippedType(), rightResultType);
                    break;
                case BoundNullCoalescingOperatorResultKind.RightType:
                    resultType = getRightResultType(leftResultType, rightResultType);
                    break;
                case BoundNullCoalescingOperatorResultKind.LeftUnwrappedRightType:
                    resultType = getRightResultType(leftResultType.StrippedType(), rightResultType);
                    break;
                case BoundNullCoalescingOperatorResultKind.RightDynamicType:
                    resultType = rightResultType;
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(node.OperatorResultKind);
            }

            bool? resultIsNullable = getIsNullable(leftOperand, leftResult) == false ? false : getIsNullable(rightOperand, rightResult);
            _result = TypeSymbolWithAnnotations.Create(resultType, resultIsNullable);
            return null;

            bool? getIsNullable(BoundExpression e, Result r) => (r.Type is null) ? e.IsNullable() : r.Type.IsNullable;
            TypeSymbol getLeftResultType(TypeSymbol leftType, TypeSymbol rightType)
            {
                // If there was an identity conversion between the two operands (in short, if there
                // is no implicit conversion on the right operand), then check nullable conversions
                // in both directions since it's possible the right operand is the better result type.
                if ((object)rightType != null &&
                    (node.RightOperand as BoundConversion)?.ExplicitCastInCode != false &&
                    GenerateConversionForConditionalOperator(node.LeftOperand, leftType, rightType, reportMismatch: false).Exists)
                {
                    return rightType;
                }
                GenerateConversionForConditionalOperator(node.RightOperand, rightType, leftType, reportMismatch: true);
                return leftType;
            }
            TypeSymbol getRightResultType(TypeSymbol leftType, TypeSymbol rightType)
            {
                GenerateConversionForConditionalOperator(node.LeftOperand, leftType, rightType, reportMismatch: true);
                return rightType;
            }
        }

        public override BoundNode VisitConditionalAccess(BoundConditionalAccess node)
        {
            Debug.Assert(!IsConditionalState);

            var receiver = node.Receiver;
            var receiverType = VisitRvalueWithResult(receiver).Type;

            var receiverState = this.State.Clone();

            if (receiver.Type?.IsReferenceType == true)
            {
                if (receiverType?.IsNullable == false)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.HDN_ExpressionIsProbablyNeverNull, receiver.Syntax);
                }

                int slot = MakeSlot(SkipReferenceConversions(receiver));
                if (slot > 0)
                {
                    if (slot >= this.State.Capacity) Normalize(ref this.State);
                    this.State[slot] = true;
                }
            }

            if (IsConstantNull(node.Receiver))
            {
                SetUnreachable();
            }

            VisitRvalue(node.AccessExpression);
            IntersectWith(ref this.State, ref receiverState);

            // PROTOTYPE(NullableReferenceTypes): Use flow analysis type rather than node.Type
            // so that nested nullability is inferred from flow analysis. See VisitConditionalOperator.
            _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: receiverType?.IsNullable | _result.Type?.IsNullable);
            // PROTOTYPE(NullableReferenceTypes): Report conversion warnings.
            return null;
        }

        public override BoundNode VisitConditionalOperator(BoundConditionalOperator node)
        {
            var isByRef = node.IsRef;

            VisitCondition(node.Condition);
            var consequenceState = this.StateWhenTrue;
            var alternativeState = this.StateWhenFalse;

            BoundExpression consequence;
            BoundExpression alternative;
            Result consequenceResult;
            Result alternativeResult;
            bool? isNullableIfReferenceType;

            if (IsConstantTrue(node.Condition))
            {
                (alternative, alternativeResult) = visitConditionalOperand(alternativeState, node.Alternative);
                (consequence, consequenceResult) = visitConditionalOperand(consequenceState, node.Consequence);
                isNullableIfReferenceType = getIsNullableIfReferenceType(consequence, consequenceResult);
            }
            else if (IsConstantFalse(node.Condition))
            {
                (consequence, consequenceResult) = visitConditionalOperand(consequenceState, node.Consequence);
                (alternative, alternativeResult) = visitConditionalOperand(alternativeState, node.Alternative);
                isNullableIfReferenceType = getIsNullableIfReferenceType(alternative, alternativeResult);
            }
            else
            {
                (consequence, consequenceResult) = visitConditionalOperand(consequenceState, node.Consequence);
                Unsplit();
                (alternative, alternativeResult) = visitConditionalOperand(alternativeState, node.Alternative);
                Unsplit();
                IntersectWith(ref this.State, ref consequenceState);
                isNullableIfReferenceType = (getIsNullableIfReferenceType(consequence, consequenceResult) | getIsNullableIfReferenceType(alternative, alternativeResult));
            }

            TypeSymbolWithAnnotations resultType;
            if (node.HasErrors)
            {
                resultType = null;
            }
            else
            {
                // Determine nested nullability using BestTypeInferrer.
                // For constant conditions, we could use the nested nullability of the particular
                // branch, but that requires using the nullability of the branch as it applies to the
                // target type. For instance, the result of the conditional in the following should
                // be `IEnumerable<object>` not `object[]`:
                //   object[] a = ...;
                //   IEnumerable<object?> b = ...;
                //   var c = true ? a : b;
                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                resultType = BestTypeInferrer.InferBestTypeForConditionalOperator(
                    createPlaceholderIfNecessary(consequence, consequenceResult),
                    createPlaceholderIfNecessary(alternative, alternativeResult),
                    _conversions,
                    out _,
                    ref useSiteDiagnostics);
                if (resultType is null)
                {
                    ReportStaticNullCheckingDiagnostics(
                        ErrorCode.WRN_NoBestNullabilityConditionalExpression,
                        node.Syntax,
                        GetTypeAsDiagnosticArgument(consequenceResult.Type?.TypeSymbol),
                        GetTypeAsDiagnosticArgument(alternativeResult.Type?.TypeSymbol));
                }
            }
            resultType = TypeSymbolWithAnnotations.Create(resultType?.TypeSymbol ?? node.Type.SetUnknownNullabilityForReferenceTypes(), isNullableIfReferenceType);

            _result = resultType;
            return null;

            bool? getIsNullableIfReferenceType(BoundExpression expr, Result result)
            {
                var type = result.Type;
                if ((object)type != null)
                {
                    return type.IsNullable;
                }
                if (expr.IsLiteralNullOrDefault())
                {
                    return true;
                }
                return null;
            }

            BoundExpression createPlaceholderIfNecessary(BoundExpression expr, Result result)
            {
                var type = result.Type;
                return type is null ?
                    expr :
                    new BoundValuePlaceholder(expr.Syntax, type.IsNullable, type.TypeSymbol);
            }

            (BoundExpression, Result) visitConditionalOperand(LocalState state, BoundExpression operand)
            {
                SetState(state);
                if (isByRef)
                {
                    VisitLvalue(operand);
                }
                else
                {
                    (operand, _) = RemoveConversion(operand, includeExplicitConversions: false);
                    Visit(operand);
                }
                return (operand, _result);
            }
        }

        public override BoundNode VisitConditionalReceiver(BoundConditionalReceiver node)
        {
            var result = base.VisitConditionalReceiver(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitCall(BoundCall node)
        {
            var method = node.Method;

            if (method.CallsAreOmitted(node.SyntaxTree))
            {
                // PROTOTYPE(NullableReferenceTypes): Should skip state set in
                // arguments of omitted call. See PreciseAbstractFlowPass.VisitCall.
            }

            var receiverOpt = node.ReceiverOpt;
            if (receiverOpt != null && method.MethodKind != MethodKind.Constructor)
            {
                VisitRvalue(receiverOpt);
                CheckPossibleNullReceiver(receiverOpt);
                // PROTOTYPE(NullableReferenceTypes): Update method based on inferred receiver type.
            }

            // PROTOTYPE(NullableReferenceTypes): Can we handle some error cases?
            // (Compare with CSharpOperationFactory.CreateBoundCallOperation.)
            if (!node.HasErrors)
            {
                ImmutableArray<RefKind> refKindsOpt = node.ArgumentRefKindsOpt;
                (ImmutableArray<BoundExpression> arguments, ImmutableArray<Conversion> conversions) = RemoveArgumentConversions(node.Arguments, refKindsOpt);
                ImmutableArray<int> argsToParamsOpt = node.ArgsToParamsOpt;

                method = VisitArguments(node, arguments, refKindsOpt, method.Parameters, argsToParamsOpt, node.Expanded, method, conversions);
            }

            UpdateStateForCall(node);

            if (method.MethodKind == MethodKind.LocalFunction)
            {
                var localFunc = (LocalFunctionSymbol)method.OriginalDefinition;
                ReplayReadsAndWrites(localFunc, node.Syntax, writes: true);
            }

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                _result = method.ReturnType;
            }

            return null;
        }

        /// <summary>
        /// For each argument, figure out if its corresponding parameter is annotated with NotNullWhenFalse or
        /// EnsuresNotNull.
        /// </summary>
        private static ImmutableArray<AttributeAnnotations> GetAnnotations(int numArguments,
            bool expanded, ImmutableArray<ParameterSymbol> parameters, ImmutableArray<int> argsToParamsOpt)
        {
            ArrayBuilder<AttributeAnnotations> builder = null;

            for (int i = 0; i < numArguments; i++)
            {
                (ParameterSymbol parameter, _) = GetCorrespondingParameter(i, parameters, argsToParamsOpt, expanded);
                AttributeAnnotations annotations = parameter?.FlowAnalysisAnnotations ?? AttributeAnnotations.None;

                // Ignore NotNullWhenTrue that is inapplicable
                annotations = removeInapplicableNotNullWhenSense(parameter, annotations, sense: true);

                // Ignore NotNullWhenFalse that is inapplicable
                annotations = removeInapplicableNotNullWhenSense(parameter, annotations, sense: false);

                if (annotations != AttributeAnnotations.None && builder == null)
                {
                    builder = ArrayBuilder<AttributeAnnotations>.GetInstance(numArguments);
                    builder.AddMany(AttributeAnnotations.None, i);
                }

                if (builder != null)
                {
                    builder.Add(annotations);
                }
            }

            return builder == null ? default : builder.ToImmutableAndFree();

            AttributeAnnotations removeInapplicableNotNullWhenSense(ParameterSymbol parameter, AttributeAnnotations annotations, bool sense)
            {
                var whenSense = sense ? AttributeAnnotations.NotNullWhenTrue : AttributeAnnotations.NotNullWhenFalse;
                var whenNotSense = sense ? AttributeAnnotations.NotNullWhenFalse : AttributeAnnotations.NotNullWhenTrue;

                // NotNullWhenSense (without NotNullWhenNotSense) must be applied on a bool-returning member
                if ((annotations & whenSense) != 0 &&
                    (annotations & whenNotSense) == 0 &&
                    parameter.ContainingSymbol.GetTypeOrReturnType().SpecialType != SpecialType.System_Boolean)
                {
                    annotations &= ~whenSense;
                }

                // NotNullWhenSense must be applied to a reference or unconstrained generic type
                if ((annotations & whenSense) != 0 && parameter.Type.IsValueType != false)
                {
                    annotations &= ~whenSense;
                }

                // NotNullWhenSense is inapplicable when argument corresponds to params parameter and we're in expanded form
                if ((annotations & whenSense) != 0 && expanded && ReferenceEquals(parameter, parameters.Last()))
                {
                    annotations &= ~whenSense;
                }

                return annotations;
            }
        }

        // PROTOTYPE(NullableReferenceTypes): Record in the node whether type
        // arguments were implicit, to allow for cases where the syntax is not an
        // invocation (such as a synthesized call from a query interpretation).
        private static bool HasImplicitTypeArguments(BoundExpression node)
        {
            var syntax = node.Syntax;
            if (syntax.Kind() != SyntaxKind.InvocationExpression)
            {
                // Unexpected syntax kind.
                return false;
            }
            var nameSyntax = Binder.GetNameSyntax(((InvocationExpressionSyntax)syntax).Expression, out var _);
            if (nameSyntax == null)
            {
                // Unexpected syntax kind.
                return false;
            }
            nameSyntax = nameSyntax.GetUnqualifiedName();
            return nameSyntax.Kind() != SyntaxKind.GenericName;
        }

        protected override void VisitArguments(ImmutableArray<BoundExpression> arguments, ImmutableArray<RefKind> refKindsOpt, MethodSymbol method)
        {
            // Callers should be using VisitArguments overload below.
            throw ExceptionUtilities.Unreachable;
        }

        private void VisitArguments(
            BoundExpression node,
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt,
            MethodSymbol method,
            ImmutableArray<int> argsToParamsOpt,
            bool expanded)
        {
            // PROTOTYPE(NullableReferenceTypes): What about conversions here?
            VisitArguments(node, arguments, refKindsOpt, method is null ? default : method.Parameters, argsToParamsOpt, expanded);
        }

        private void VisitArguments(
            BoundExpression node,
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt,
            PropertySymbol property,
            ImmutableArray<int> argsToParamsOpt,
            bool expanded)
        {
            // PROTOTYPE(NullableReferenceTypes): What about conversions here?
            VisitArguments(node, arguments, refKindsOpt, property is null ? default : property.Parameters, argsToParamsOpt, expanded);
        }

        /// <summary>
        /// If you pass in a method symbol, its types will be re-inferred and the re-inferred method will be returned.
        /// </summary>
        private MethodSymbol VisitArguments(
            BoundExpression node,
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<int> argsToParamsOpt,
            bool expanded,
            MethodSymbol method = null,
            ImmutableArray<Conversion> conversions = default)
        {
            Debug.Assert(!arguments.IsDefault);
            var savedState = this.State.Clone();

            // We do a first pass to work through the arguments without making any assumptions
            ImmutableArray<Result> results = VisitArgumentsEvaluate(arguments, refKindsOpt);

            if ((object)method != null && method.IsGenericMethod && HasImplicitTypeArguments(node))
            {
                method = InferMethod((BoundCall)node, method, results.SelectAsArray(r => r.Type));
                parameters = method.Parameters;
            }

            // PROTOTYPE(NullableReferenceTypes): Can we handle some error cases?
            // (Compare with CSharpOperationFactory.CreateBoundCallOperation.)
            if (!node.HasErrors && !parameters.IsDefault)
            {
                VisitArgumentConversions(arguments, conversions, refKindsOpt, parameters, argsToParamsOpt, expanded, results);
            }

            // We do a second pass through the arguments, ignoring any diagnostics produced, but honoring the annotations,
            // to get the proper result state.
            ImmutableArray<AttributeAnnotations> annotations = GetAnnotations(arguments.Length, expanded, parameters, argsToParamsOpt);

            if (!annotations.IsDefault)
            {
                this.SetState(savedState);

                bool saveDisableDiagnostics = _disableDiagnostics;
                _disableDiagnostics = true;
                if (!node.HasErrors && !parameters.IsDefault)
                {
                    VisitArgumentConversions(arguments, conversions, refKindsOpt, parameters, argsToParamsOpt, expanded, results); // recompute out vars after state was reset
                }
                VisitArgumentsEvaluateHonoringAnnotations(arguments, refKindsOpt, annotations);

                _disableDiagnostics = saveDisableDiagnostics;
            }

            return method;
        }

        private ImmutableArray<Result> VisitArgumentsEvaluate(
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt)
        {
            Debug.Assert(!IsConditionalState);
            int n = arguments.Length;
            if (n == 0)
            {
                return ImmutableArray<Result>.Empty;
            }
            var builder = ArrayBuilder<Result>.GetInstance(n);
            for (int i = 0; i < n; i++)
            {
                VisitArgumentEvaluate(arguments, refKindsOpt, i);
                builder.Add(_result);
            }

            _result = _invalidType;
            return builder.ToImmutableAndFree();
        }

        private void VisitArgumentEvaluate(ImmutableArray<BoundExpression> arguments, ImmutableArray<RefKind> refKindsOpt, int i)
        {
            RefKind refKind = GetRefKind(refKindsOpt, i);
            var argument = arguments[i];
            if (refKind != RefKind.Out)
            {
                // PROTOTYPE(NullReferenceTypes): `ref` arguments should be treated as l-values
                // for assignment. See `ref x3` in StaticNullChecking.PassingParameters_01.
                VisitRvalue(argument);
            }
            else
            {
                VisitLvalue(argument);
            }
        }

        /// <summary>
        /// Visit all the arguments for the purpose of computing the exit state of the method,
        /// given the annotations.
        /// </summary>
        private void VisitArgumentsEvaluateHonoringAnnotations(
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt,
            ImmutableArray<AttributeAnnotations> annotations)
        {
            Debug.Assert(!IsConditionalState);
            Debug.Assert(annotations.Length == arguments.Length);
            Debug.Assert(_disableDiagnostics);

            for (int i = 0; i < arguments.Length; i++)
            {
                if (this.IsConditionalState)
                {
                    // We could be in a conditional state because of a conditional annotation (like NotNullWhenFalse)
                    // Then WhenTrue/False states correspond to the invocation returning true/false

                    // We'll assume that we're in the unconditional state where the method returns true,
                    // then we'll repeat assuming the method returns false.

                    LocalState whenTrue = this.StateWhenTrue.Clone();
                    LocalState whenFalse = this.StateWhenFalse.Clone();

                    this.SetState(whenTrue);
                    VisitArgumentEvaluate(arguments, refKindsOpt, i);
                    Debug.Assert(!IsConditionalState);
                    whenTrue = this.State; // LocalState may be a struct

                    this.SetState(whenFalse);
                    VisitArgumentEvaluate(arguments, refKindsOpt, i);
                    Debug.Assert(!IsConditionalState);
                    whenFalse = this.State; // LocalState may be a struct

                    this.SetConditionalState(whenTrue, whenFalse);
                }
                else
                {
                    VisitArgumentEvaluate(arguments, refKindsOpt, i);
                }

                var argument = arguments[i];
                if (argument.Type?.IsReferenceType != true)
                {
                    continue;
                }

                int slot = MakeSlot(argument);
                if (slot <= 0)
                {
                    continue;
                }

                AttributeAnnotations annotation = annotations[i];
                bool notNullWhenTrue = (annotation & AttributeAnnotations.NotNullWhenTrue) != 0;
                bool notNullWhenFalse = (annotation & AttributeAnnotations.NotNullWhenFalse) != 0;
                // The WhenTrue/False states correspond to the invocation returning true/false
                bool wasPreviouslySplit = this.IsConditionalState;
                Split();
                if (notNullWhenTrue)
                {
                    this.StateWhenTrue[slot] = true;
                }
                if (notNullWhenFalse)
                {
                    this.StateWhenFalse[slot] = true;
                    if (notNullWhenTrue && !wasPreviouslySplit) Unsplit();
                }
            }

            _result = _invalidType;
        }

        private void VisitArgumentConversions(
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<Conversion> conversions,
            ImmutableArray<RefKind> refKindsOpt,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<int> argsToParamsOpt,
            bool expanded,
            ImmutableArray<Result> results)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                (ParameterSymbol parameter, TypeSymbolWithAnnotations parameterType) = GetCorrespondingParameter(i, parameters, argsToParamsOpt, expanded);
                if (parameter is null)
                {
                    continue;
                }
                VisitArgumentConversion(
                    arguments[i],
                    conversions.IsDefault ? Conversion.Identity : conversions[i],
                    GetRefKind(refKindsOpt, i),
                    parameter,
                    parameterType,
                    results[i]);
            }
        }

        /// <summary>
        /// Report warnings for an argument corresponding to a specific parameter.
        /// </summary>
        private void VisitArgumentConversion(
            BoundExpression argument,
            Conversion conversion,
            RefKind refKind,
            ParameterSymbol parameter,
            TypeSymbolWithAnnotations parameterType,
            Result result)
        {
            TypeSymbolWithAnnotations resultType = result.Type;
            var argumentType = resultType?.TypeSymbol;
            switch (refKind)
            {
                case RefKind.None:
                case RefKind.In:
                    {
                        resultType = ApplyConversion(argument, argument, conversion, parameterType.TypeSymbol, resultType, checkConversion: true, fromExplicitCast: false, out bool canConvertNestedNullability);
                        if (!ReportNullReferenceArgumentIfNecessary(argument, resultType, parameter, parameterType) &&
                            !canConvertNestedNullability)
                        {
                            ReportNullabilityMismatchInArgument(argument, argumentType, parameter, parameterType.TypeSymbol);
                        }
                    }
                    break;
                case RefKind.Out:
                    if (argument is BoundLocal local && local.DeclarationKind == BoundLocalDeclarationKind.WithInferredType)
                    {
                        _variableTypes[local.LocalSymbol] = parameterType;
                        resultType = parameterType;
                    }
                    if (!ReportNullReferenceAssignmentIfNecessary(argument, resultType, parameterType, useLegacyWarnings: UseLegacyWarnings(argument)))
                    {
                        HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                        if (!_conversions.HasIdentityOrImplicitReferenceConversion(parameterType.TypeSymbol, argumentType, ref useSiteDiagnostics))
                        {
                            ReportNullabilityMismatchInArgument(argument, argumentType, parameter, parameterType.TypeSymbol);
                        }
                    }
                    // Set nullable state of argument to parameter type.
                    TrackNullableStateForAssignment(argument, resultType, result.Slot, parameterType);
                    break;
                case RefKind.Ref:
                    if (!ReportNullReferenceArgumentIfNecessary(argument, resultType, parameter, parameterType) &&
                        !ReportNullReferenceAssignmentIfNecessary(argument, resultType, parameterType, useLegacyWarnings: UseLegacyWarnings(argument)))
                    {
                        if ((object)argumentType != null && IsNullabilityMismatch(argumentType, parameterType.TypeSymbol))
                        {
                            ReportNullabilityMismatchInArgument(argument, argumentType, parameter, parameterType.TypeSymbol);
                        }
                    }
                    // Set nullable state of argument to parameter type.
                    TrackNullableStateForAssignment(argument, resultType, result.Slot, parameterType);
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(refKind);
            }
        }

        private static (ImmutableArray<BoundExpression> Arguments, ImmutableArray<Conversion> Conversions) RemoveArgumentConversions(
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> refKindsOpt)
        {
            int n = arguments.Length;
            var conversions = default(ImmutableArray<Conversion>);
            if (n > 0)
            {
                var argumentsBuilder = ArrayBuilder<BoundExpression>.GetInstance(n);
                var conversionsBuilder = ArrayBuilder<Conversion>.GetInstance(n);
                bool includedConversion = false;
                for (int i = 0; i < n; i++)
                {
                    RefKind refKind = GetRefKind(refKindsOpt, i);
                    var argument = arguments[i];
                    var conversion = Conversion.Identity;
                    // PROTOTYPE(NullableReferenceTypes): Should `RefKind.In` be treated similarly to `RefKind.None`?
                    if (refKind == RefKind.None)
                    {
                        var before = argument;
                        (argument, conversion) = RemoveConversion(argument, includeExplicitConversions: false);
                        if (argument != before)
                        {
                            includedConversion = true;
                        }
                    }
                    argumentsBuilder.Add(argument);
                    conversionsBuilder.Add(conversion);
                }
                if (includedConversion)
                {
                    arguments = argumentsBuilder.ToImmutable();
                    conversions = conversionsBuilder.ToImmutable();
                }
                argumentsBuilder.Free();
                conversionsBuilder.Free();
            }
            return (arguments, conversions);
        }

        private static (ParameterSymbol Parameter, TypeSymbolWithAnnotations Type) GetCorrespondingParameter(
            int argumentOrdinal,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<int> argsToParamsOpt,
            bool expanded)
        {
            if (parameters.IsDefault)
            {
                return (null, null);
            }

            int n = parameters.Length;
            ParameterSymbol parameter;

            if (argsToParamsOpt.IsDefault)
            {
                if (argumentOrdinal < n)
                {
                    parameter = parameters[argumentOrdinal];
                }
                else if (expanded)
                {
                    parameter = parameters[n - 1];
                }
                else
                {
                    parameter = null;
                }
            }
            else
            {
                int parameterOrdinal = argsToParamsOpt[argumentOrdinal];

                if (parameterOrdinal < n)
                {
                    parameter = parameters[parameterOrdinal];
                }
                else
                {
                    parameter = null;
                    expanded = false;
                }
            }

            if (parameter is null)
            {
                Debug.Assert(!expanded);
                return (null, null);
            }

            var type = parameter.Type;
            if (expanded && parameter.Ordinal == n - 1 && parameter.Type.IsSZArray())
            {
                type = ((ArrayTypeSymbol)type.TypeSymbol).ElementType;
            }

            return (parameter, type);
        }

        private MethodSymbol InferMethod(BoundCall node, MethodSymbol method, ImmutableArray<TypeSymbolWithAnnotations> argumentTypes)
        {
            Debug.Assert(method.IsGenericMethod);
            // PROTOTYPE(NullableReferenceTypes): OverloadResolution.IsMemberApplicableInNormalForm and
            // IsMemberApplicableInExpandedForm use the least overridden method. We need to do the same here.
            var definition = method.ConstructedFrom;
            // PROTOTYPE(NullableReferenceTypes): MethodTypeInferrer.Infer relies
            // on the BoundExpressions for tuple element types and method groups.
            // By using a generic BoundValuePlaceholder, we're losing inference in those cases.
            // PROTOTYPE(NullableReferenceTypes): Inference should be based on
            // unconverted arguments. Consider cases such as `default`, lambdas, tuples.
            ImmutableArray<BoundExpression> arguments = argumentTypes.ZipAsArray(node.Arguments, s_makePlaceholderForArgumentFunc);

            var refKinds = ArrayBuilder<RefKind>.GetInstance();
            if (node.ArgumentRefKindsOpt != null)
            {
                refKinds.AddRange(node.ArgumentRefKindsOpt);
            }
            OverloadResolution.GetEffectiveParameterTypes(
                definition,
                node.Arguments.Length,
                node.ArgsToParamsOpt,
                refKinds,
                isMethodGroupConversion: false,
                // PROTOTYPE(NullableReferenceTypes): `allowRefOmittedArguments` should be
                // false for constructors and several other cases (see Binder use). Should we
                // capture the original value in the BoundCall?
                allowRefOmittedArguments: true,
                binder: _binder,
                expanded: node.Expanded,
                parameterTypes: out ImmutableArray<TypeSymbolWithAnnotations> parameterTypes,
                parameterRefKinds: out ImmutableArray<RefKind> parameterRefKinds);
            refKinds.Free();
            HashSet<DiagnosticInfo> useSiteDiagnostics = null;
            var result = MethodTypeInferrer.Infer(
                _binder,
                _conversions,
                definition.TypeParameters,
                definition.ContainingType,
                parameterTypes,
                parameterRefKinds,
                arguments,
                ref useSiteDiagnostics);
            if (result.Success)
            {
                // PROTOTYPE(NullableReferenceTypes): Report conversion warnings.
                return definition.Construct(result.InferredTypeArguments);
            }
            return method;
        }

        private static readonly Func<TypeSymbolWithAnnotations, BoundExpression, BoundExpression> s_makePlaceholderForArgumentFunc =
            (TypeSymbolWithAnnotations argumentType, BoundExpression argument) =>
            {
                if (argumentType is null)
                {
                    return argument;
                }

                if (argument is BoundLocal local && local.DeclarationKind == BoundLocalDeclarationKind.WithInferredType)
                {
                    // 'out var' doesn't contribute to inference
                    return new BoundValuePlaceholder(argument.Syntax, isNullable: null, type: null);
                }

                return new BoundValuePlaceholder(argument.Syntax, argumentType.IsNullable, argumentType.TypeSymbol);
            };

        private void ReplayReadsAndWrites(LocalFunctionSymbol localFunc,
                                  SyntaxNode syntax,
                                  bool writes)
        {
            // PROTOTYPE(NullableReferenceTypes): Support field initializers in local functions.
        }

        /// <summary>
        /// Returns the expression without the top-most conversion plus the conversion.
        /// If the expression is not a conversion, returns the original expression plus
        /// the Identity conversion. If `includeExplicitConversions` is true, implicit and
        /// explicit conversions are considered. If `includeExplicitConversions` is false
        /// only implicit conversions are considered and if the expression is an explicit
        /// conversion, the expression is returned as is, with the Identity conversion.
        /// (Currently, the only visit method that passes `includeExplicitConversions: true`
        /// is VisitConversion. All other callers are handling implicit conversions only.)
        /// </summary>
        private static (BoundExpression Expression, Conversion Conversion) RemoveConversion(BoundExpression expr, bool includeExplicitConversions)
        {
            ConversionGroup group = null;
            while (true)
            {
                if (expr.Kind != BoundKind.Conversion)
                {
                    break;
                }
                var conversion = (BoundConversion)expr;
                if (group != conversion.ConversionGroupOpt && group != null)
                {
                    // E.g.: (C)(B)a
                    break;
                }
                group = conversion.ConversionGroupOpt;
                Debug.Assert(group != null || !conversion.ExplicitCastInCode); // Explicit conversions should include a group.
                if (!includeExplicitConversions && group?.IsExplicitConversion == true)
                {
                    return (expr, Conversion.Identity);
                }
                expr = conversion.Operand;
                if (group == null)
                {
                    // Ungrouped conversion should not be followed by another ungrouped
                    // conversion. Otherwise, the conversions should have been grouped.
                    Debug.Assert(expr.Kind != BoundKind.Conversion ||
                        ((BoundConversion)expr).ConversionGroupOpt != null ||
                        ((BoundConversion)expr).ConversionKind == ConversionKind.NoConversion);
                    return (expr, conversion.Conversion);
                }
            }
            return (expr, group?.Conversion ?? Conversion.Identity);
        }

        // See Binder.BindNullCoalescingOperator for initial binding.
        private Conversion GenerateConversionForConditionalOperator(BoundExpression sourceExpression, TypeSymbol sourceType, TypeSymbol destinationType, bool reportMismatch)
        {
            var conversion = GenerateConversion(_conversions, sourceExpression, sourceType, destinationType);
            bool canConvertNestedNullability = conversion.Exists;
            if (!canConvertNestedNullability && reportMismatch)
            {
                ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, sourceExpression.Syntax, GetTypeAsDiagnosticArgument(sourceType), destinationType);
            }
            return conversion;
        }

        private static Conversion GenerateConversion(Conversions conversions, BoundExpression sourceExpression, TypeSymbol sourceType, TypeSymbol destinationType, bool fromExplicitCast = false)
        {
            HashSet<DiagnosticInfo> useSiteDiagnostics = null;
            return UseExpressionForConversion(sourceExpression) ?
                (fromExplicitCast ?
                    conversions.ClassifyConversionFromExpression(sourceExpression, destinationType, ref useSiteDiagnostics, forCast: true) :
                    conversions.ClassifyImplicitConversionFromExpression(sourceExpression, destinationType, ref useSiteDiagnostics)) :
                (fromExplicitCast ?
                    conversions.ClassifyConversionFromType(sourceType, destinationType, ref useSiteDiagnostics, forCast: true) :
                    conversions.ClassifyImplicitConversionFromType(sourceType, destinationType, ref useSiteDiagnostics));
        }

        /// <summary>
        /// Returns true if the expression should be used as the source when calculating
        /// a conversion from this expression, rather than using the type (with nullability)
        /// calculated by visiting this expression. Typically, that means expressions that
        /// do not have an explicit type but there are several other cases as well.
        /// (See expressions handled in ClassifyImplicitBuiltInConversionFromExpression.)
        /// </summary>
        private static bool UseExpressionForConversion(BoundExpression value)
        {
            if (value is null)
            {
                return false;
            }
            if (value.Type is null || value.Type.IsDynamic() || value.ConstantValue != null)
            {
                return true;
            }
            switch (value.Kind)
            {
                case BoundKind.InterpolatedString:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Adjust declared type based on inferred nullability at the point of reference.
        /// </summary>
        private Result GetAdjustedResult(Result pair)
        {
            var type = pair.Type;
            var slot = pair.Slot;
            if (slot > 0 && slot < this.State.Capacity)
            {
                bool? isNullable = !this.State[slot];
                if (isNullable != type.IsNullable)
                {
                    return Result.Create(TypeSymbolWithAnnotations.Create(type.TypeSymbol, isNullable), slot);
                }
            }
            return pair;
        }

        private Symbol AsMemberOfResultType(Symbol symbol)
        {
            var containingType = _result.Type?.TypeSymbol as NamedTypeSymbol;
            if ((object)containingType == null || containingType.IsErrorType())
            {
                return symbol;
            }
            return AsMemberOfType(containingType, symbol);
        }

        private static Symbol AsMemberOfType(NamedTypeSymbol containingType, Symbol symbol)
        {
            if (symbol is null)
            {
                return null;
            }
            if (symbol.Kind == SymbolKind.Method)
            {
                if (((MethodSymbol)symbol).MethodKind == MethodKind.LocalFunction)
                {
                    // PROTOTYPE(NullableReferenceTypes): Handle type substitution for local functions.
                    return symbol;
                }
            }
            var symbolDef = symbol.OriginalDefinition;
            var symbolDefContainer = symbolDef.ContainingType;
            while (true)
            {
                if (containingType.OriginalDefinition.Equals(symbolDefContainer, TypeCompareKind.ConsiderEverything))
                {
                    if (symbolDefContainer.IsTupleType)
                    {
                        return AsMemberOfTupleType((TupleTypeSymbol)containingType, symbol);
                    }
                    return symbolDef.SymbolAsMember(containingType);
                }
                containingType = containingType.BaseTypeNoUseSiteDiagnostics;
                if ((object)containingType == null)
                {
                    break;
                }
            }
            // PROTOTYPE(NullableReferenceTypes): Handle other cases such as interfaces.
            Debug.Assert(symbolDefContainer.IsInterface);
            return symbol;
        }

        private static Symbol AsMemberOfTupleType(TupleTypeSymbol tupleType, Symbol symbol)
        {
            if (symbol.ContainingType.Equals(tupleType, TypeCompareKind.CompareNullableModifiersForReferenceTypes))
            {
                return symbol;
            }
            switch (symbol.Kind)
            {
                case SymbolKind.Field:
                    {
                        var index = ((FieldSymbol)symbol).TupleElementIndex;
                        if (index >= 0)
                        {
                            return tupleType.TupleElements[index];
                        }
                        return tupleType.GetTupleMemberSymbolForUnderlyingMember(((TupleFieldSymbol)symbol).UnderlyingField);
                    }
                case SymbolKind.Property:
                    return tupleType.GetTupleMemberSymbolForUnderlyingMember(((TuplePropertySymbol)symbol).UnderlyingProperty);
                case SymbolKind.Event:
                    return tupleType.GetTupleMemberSymbolForUnderlyingMember(((TupleEventSymbol)symbol).UnderlyingEvent);
                default:
                    throw ExceptionUtilities.UnexpectedValue(symbol.Kind);
            }
        }

        public override BoundNode VisitConversion(BoundConversion node)
        {
            if (node.ConversionKind == ConversionKind.MethodGroup
                && node.SymbolOpt?.MethodKind == MethodKind.LocalFunction)
            {
                var localFunc = (LocalFunctionSymbol)node.SymbolOpt.OriginalDefinition;
                var syntax = node.Syntax;
                ReplayReadsAndWrites(localFunc, syntax, writes: false);
            }

            (BoundExpression operand, Conversion conversion) = RemoveConversion(node, includeExplicitConversions: true);
            Debug.Assert(operand != null);

            Visit(operand);
            TypeSymbolWithAnnotations operandType = _result.Type;
            TypeSymbolWithAnnotations explicitType = node.ConversionGroupOpt?.ExplicitType;
            bool fromExplicitCast = (object)explicitType != null;
            TypeSymbolWithAnnotations resultType = ApplyConversion(node, operand, conversion, explicitType?.TypeSymbol ?? node.Type, operandType, checkConversion: !fromExplicitCast, fromExplicitCast: fromExplicitCast, out bool _);

            if (fromExplicitCast && explicitType.IsNullable == false)
            {
                TypeSymbol targetType = explicitType.TypeSymbol;
                bool reportNullable = false;
                if (targetType.IsReferenceType && IsUnconstrainedTypeParameter(resultType?.TypeSymbol))
                {
                    reportNullable = true;
                }
                else if ((targetType.IsReferenceType || IsUnconstrainedTypeParameter(targetType)) &&
                    resultType?.IsNullable == true)
                {
                    reportNullable = true;
                }
                if (reportNullable)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_ConvertingNullableToNonNullable, node.Syntax);
                }
            }

            _result = resultType;
            return null;
        }

        public override BoundNode VisitTupleLiteral(BoundTupleLiteral node)
        {
            VisitTupleExpression(node);
            return null;
        }

        public override BoundNode VisitConvertedTupleLiteral(BoundConvertedTupleLiteral node)
        {
            VisitTupleExpression(node);
            return null;
        }

        private void VisitTupleExpression(BoundTupleExpression node)
        {
            var arguments = node.Arguments;
            ImmutableArray<TypeSymbolWithAnnotations> elementTypes = arguments.SelectAsArray((a, w) => w.VisitRvalueWithResult(a).Type, this);
            var tupleOpt = (TupleTypeSymbol)node.Type;
            _result = (tupleOpt is null) ?
                null :
                TypeSymbolWithAnnotations.Create(tupleOpt.WithElementTypes(elementTypes));
        }

        public override BoundNode VisitTupleBinaryOperator(BoundTupleBinaryOperator node)
        {
            base.VisitTupleBinaryOperator(node);
            SetResult(node);
            return null;
        }

        private void ReportNullabilityMismatchWithTargetDelegate(SyntaxNode syntax, NamedTypeSymbol delegateType, MethodSymbol method)
        {
            if ((object)delegateType == null || (object)method == null)
            {
                return;
            }

            MethodSymbol invoke = delegateType.DelegateInvokeMethod;

            if ((object)invoke == null)
            {
                return;
            }

            if (IsNullabilityMismatch(invoke.ReturnType, method.ReturnType))
            {
                ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInReturnTypeOfTargetDelegate, syntax,
                    new FormattedSymbol(method, SymbolDisplayFormat.MinimallyQualifiedFormat),
                    delegateType);
            }

            int count = Math.Min(invoke.ParameterCount, method.ParameterCount);

            for (int i = 0; i < count; i++)
            {
                if (IsNullabilityMismatch(invoke.Parameters[i].Type, method.Parameters[i].Type))
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInParameterTypeOfTargetDelegate, syntax,
                        new FormattedSymbol(method.Parameters[i], SymbolDisplayFormat.ShortFormat),
                        new FormattedSymbol(method, SymbolDisplayFormat.MinimallyQualifiedFormat),
                        delegateType);
                }
            }
        }

        /// <summary>
        /// Re-calculate and apply the conversion to the type of the operand and return the resulting type.
        /// </summary>
        private TypeSymbolWithAnnotations ApplyConversion(BoundExpression operand, Conversion conversion, TypeSymbol targetType, TypeSymbolWithAnnotations operandType)
        {
            Debug.Assert(operand != null);
            return ApplyConversion(operand, operand, conversion, targetType, operandType, checkConversion: true, fromExplicitCast: false, out _);
        }

        /// <summary>
        /// Apply the conversion to the type of the operand and return the resulting type. (If the
        /// operand does not have an explicit type, the operand expression is used for the type.)
        /// If `checkConversion` is set, the incoming conversion is assumed to be from binding and will be
        /// re-calculated, this time considering nullability. (Note that the conversion calculation considers
        /// nested nullability only. The caller is responsible for checking the top-level nullability of
        /// the type returned by this method.) `canConvertNestedNullability` is set if the conversion
        /// considering nested nullability succeeded. `node` is used only for the location of diagnostics.
        /// </summary>
        private TypeSymbolWithAnnotations ApplyConversion(
            BoundNode node,
            BoundExpression operandOpt,
            Conversion conversion,
            TypeSymbol targetType,
            TypeSymbolWithAnnotations operandType,
            bool checkConversion,
            bool fromExplicitCast,
            out bool canConvertNestedNullability)
        {
            Debug.Assert(node != null);
            Debug.Assert(operandOpt != null || (object)operandType != null);
            Debug.Assert((object)targetType != null);

            bool? isNullableIfReferenceType = null;
            canConvertNestedNullability = true;

            switch (conversion.Kind)
            {
                case ConversionKind.MethodGroup:
                    if (!fromExplicitCast)
                    {
                        ReportNullabilityMismatchWithTargetDelegate(operandOpt.Syntax, targetType.GetDelegateType(), conversion.Method);
                    }
                    isNullableIfReferenceType = false;
                    break;

                case ConversionKind.AnonymousFunction:
                case ConversionKind.InterpolatedString:
                    isNullableIfReferenceType = false;
                    break;

                case ConversionKind.ExplicitUserDefined:
                case ConversionKind.ImplicitUserDefined:
                    // cf. Binder.CreateUserDefinedConversion
                    {
                        if (!conversion.IsValid)
                        {
                            break;
                        }

                        // operand -> conversion "from" type
                        operandType = ApplyConversion(
                            node,
                            operandOpt,
                            conversion.UserDefinedFromConversion,
                            conversion.BestUserDefinedConversionAnalysis.FromType,
                            operandType,
                            checkConversion: false,
                            fromExplicitCast: false,
                            out _);

                        // PROTOTYPE(NullableReferenceTypes): Update method based on operandType
                        // (see StaticNullChecking_FlowAnalysis.Conversions_07).
                        var methodOpt = conversion.Method;
                        Debug.Assert((object)methodOpt != null);
                        Debug.Assert(methodOpt.ParameterCount == 1);
                        var parameter = methodOpt.Parameters[0];

                        // conversion "from" type -> method parameter type
                        operandType = ClassifyAndApplyConversion(node, parameter.Type.TypeSymbol, operandType);
                        ReportNullReferenceArgumentIfNecessary(operandOpt, operandType, parameter, parameter.Type);

                        // method parameter type -> method return type
                        operandType = methodOpt.ReturnType;

                        // method return type -> conversion "to" type
                        operandType = ClassifyAndApplyConversion(node, conversion.BestUserDefinedConversionAnalysis.ToType, operandType);

                        // conversion "to" type -> final type
                        // PROTOTYPE(NullableReferenceTypes): If the original conversion was
                        // explicit, this conversion should not report nested nullability mismatches.
                        // (see StaticNullChecking.ExplicitCast_UserDefined_02).
                        operandType = ClassifyAndApplyConversion(node, targetType, operandType);
                        return operandType;
                    }

                case ConversionKind.ExplicitDynamic:
                case ConversionKind.ImplicitDynamic:
                    isNullableIfReferenceType = operandType?.IsNullable;
                    break;

                case ConversionKind.Unboxing:
                case ConversionKind.ImplicitThrow:
                    break;

                case ConversionKind.Boxing:
                    if (operandType?.IsValueType == true)
                    {
                        // PROTOTYPE(NullableReferenceTypes): Should we worry about a pathological case of boxing nullable value known to be not null?
                        //       For example, new int?(0)
                        isNullableIfReferenceType = operandType.IsNullableType();
                    }
                    else if (IsUnconstrainedTypeParameter(operandType?.TypeSymbol))
                    {
                        isNullableIfReferenceType = true;
                    }
                    else
                    {
                        Debug.Assert(operandType?.IsReferenceType != true ||
                            operandType.SpecialType == SpecialType.System_ValueType ||
                            operandType.TypeKind == TypeKind.Interface ||
                            operandType.TypeKind == TypeKind.Dynamic);
                    }
                    break;

                case ConversionKind.NoConversion:
                case ConversionKind.DefaultOrNullLiteral:
                    checkConversion = false;
                    goto case ConversionKind.Identity;

                case ConversionKind.Identity:
                case ConversionKind.ImplicitReference:
                case ConversionKind.ExplicitReference:
                    if (operandType is null && operandOpt.IsLiteralNullOrDefault())
                    {
                        isNullableIfReferenceType = true;
                    }
                    else
                    {
                        // Inherit state from the operand.
                        if (checkConversion)
                        {
                            // PROTOTYPE(NullableReferenceTypes): Assert conversion is similar to original.
                            conversion = GenerateConversion(_conversions, operandOpt, operandType?.TypeSymbol, targetType, fromExplicitCast);
                            canConvertNestedNullability = conversion.Exists;
                        }
                        isNullableIfReferenceType = operandType?.IsNullable;
                    }
                    break;

                case ConversionKind.Deconstruction:
                    // Can reach here, with an error type, when the
                    // Deconstruct method is missing or inaccessible.
                    break;

                case ConversionKind.ExplicitEnumeration:
                    // Can reach here, with an error type.
                    break;

                default:
                    Debug.Assert(targetType.IsReferenceType != true);
                    break;
            }

            return TypeSymbolWithAnnotations.Create(targetType, isNullableIfReferenceType);
        }

        private TypeSymbolWithAnnotations ClassifyAndApplyConversion(BoundNode node, TypeSymbol targetType, TypeSymbolWithAnnotations operandType)
        {
            HashSet<DiagnosticInfo> useSiteDiagnostics = null;
            var conversion = _conversions.ClassifyStandardConversion(null, operandType.TypeSymbol, targetType, ref useSiteDiagnostics);
            if (!conversion.Exists)
            {
                // PROTOTYPE(NullableReferenceTypes): Not necessarily an assignment
                // (see StaticNullChecking_FlowAnalysis.Conversions_07).
                ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, node.Syntax, operandType.TypeSymbol, targetType);
            }
            return ApplyConversion(node, operandOpt: null, conversion, targetType, operandType, checkConversion: false, fromExplicitCast: false, out _);
        }

        public override BoundNode VisitDelegateCreationExpression(BoundDelegateCreationExpression node)
        {
            if (node.MethodOpt?.MethodKind == MethodKind.LocalFunction)
            {
                var syntax = node.Syntax;
                var localFunc = (LocalFunctionSymbol)node.MethodOpt.OriginalDefinition;
                ReplayReadsAndWrites(localFunc, syntax, writes: false);
            }

            base.VisitDelegateCreationExpression(node);
            SetResult(node);
            return null;
        }

        public override BoundNode VisitMethodGroup(BoundMethodGroup node)
        {
            Debug.Assert(!IsConditionalState);

            BoundExpression receiverOpt = node.ReceiverOpt;
            if (receiverOpt != null)
            {
                // An explicit or implicit receiver, for example in an expression such as (x.Foo is Action, or Foo is Action), is considered to be read.
                VisitRvalue(receiverOpt);

                CheckPossibleNullReceiver(receiverOpt);
            }

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                _result = null;
            }

            return null;
        }

        public override BoundNode VisitLambda(BoundLambda node)
        {
            var result = VisitLambdaOrLocalFunction(node);
            SetResult(node); // PROTOTYPE(NullableReferenceTypes): Conversions: Lamba
            return result;
        }

        public override BoundNode VisitUnboundLambda(UnboundLambda node)
        {
            var result = base.VisitUnboundLambda(node);
            SetResult(node);
            return result;
        }

        private BoundNode VisitLambdaOrLocalFunction(IBoundLambdaOrFunction node)
        {
            var oldMethodOrLambda = this._currentMethodOrLambda;
            this._currentMethodOrLambda = node.Symbol;

            var oldPending = SavePending(); // we do not support branches into a lambda
            LocalState finalState = this.State;
            this.State = this.State.Reachable ? this.State.Clone() : AllBitsSet();
            if (!node.WasCompilerGenerated) EnterParameters(node.Symbol.Parameters);
            var oldPending2 = SavePending();
            VisitAlways(node.Body);
            RestorePending(oldPending2); // process any forward branches within the lambda body
            ImmutableArray<PendingBranch> pendingReturns = RemoveReturns();
            RestorePending(oldPending);
            IntersectWith(ref finalState, ref this.State); // a no-op except in region analysis
            _result = _invalidType;
            foreach (PendingBranch pending in pendingReturns)
            {
                this.State = pending.State;
                IntersectWith(ref finalState, ref this.State); // a no-op except in region analysis
                _result = _invalidType;
            }

            this.State = finalState;

            this._currentMethodOrLambda = oldMethodOrLambda;
            return null;
        }

        public override BoundNode VisitThisReference(BoundThisReference node)
        {
            VisitThisOrBaseReference(node);
            return null;
        }

        private void VisitThisOrBaseReference(BoundExpression node)
        {
            var thisParameter = MethodThisParameter;
            int slot = (object)thisParameter == null ? -1 : GetOrCreateSlot(thisParameter);
            _result = Result.Create(TypeSymbolWithAnnotations.Create(node.Type), slot);
        }

        public override BoundNode VisitParameter(BoundParameter node)
        {
            _result = GetAdjustedResult(GetDeclaredParameterResult(node.ParameterSymbol));
            return null;
        }

        public override BoundNode VisitAssignmentOperator(BoundAssignmentOperator node)
        {
            Debug.Assert(!IsConditionalState);

            var left = node.Left;
            VisitLvalue(left);
            Result leftResult = _result;

            (BoundExpression right, Conversion conversion) = RemoveConversion(node.Right, includeExplicitConversions: false);
            VisitRvalue(right);
            Result rightResult = _result;

            if (left.Kind == BoundKind.EventAccess && ((BoundEventAccess)left).EventSymbol.IsWindowsRuntimeEvent)
            {
                // Event assignment is a call to an Add method. (Note that assignment
                // of non-field-like events uses BoundEventAssignmentOperator
                // rather than BoundAssignmentOperator.)
                SetResult(node);
            }
            else
            {
                TypeSymbolWithAnnotations leftType = leftResult.Type;
                TypeSymbolWithAnnotations rightType = ApplyConversion(right, right, conversion, leftType.TypeSymbol, rightResult.Type, checkConversion: true, fromExplicitCast: false, out bool canConvertNestedNullability);
                // Need to report all warnings that apply since the warnings can be suppressed individually.
                ReportNullReferenceAssignmentIfNecessary(right, leftType, rightType, UseLegacyWarnings(left));
                if (!canConvertNestedNullability)
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInAssignment, right.Syntax, GetTypeAsDiagnosticArgument(rightResult.Type?.TypeSymbol), leftType.TypeSymbol);
                }
                TrackNullableStateForAssignment(right, leftType, leftResult.Slot, rightType, rightResult.Slot);
                // PROTOTYPE(NullableReferenceTypes): Check node.Type.IsErrorType() instead?
                _result = node.HasErrors ? TypeSymbolWithAnnotations.Create(node.Type) : rightType;
            }

            return null;
        }

        private static bool UseLegacyWarnings(BoundExpression expr)
        {
            switch (expr.Kind)
            {
                case BoundKind.Local:
                case BoundKind.Parameter:
                    // PROTOTYPE(NullableReferenceTypes): Warnings when assigning to `ref`
                    // or `out` parameters should be regular warnings. Warnings assigning to
                    // other parameters should be W warnings.
                    return true;
                default:
                    return false;
            }
        }

        public override BoundNode VisitDeconstructionAssignmentOperator(BoundDeconstructionAssignmentOperator node)
        {
            // PROTOTYPE(NullableReferenceTypes): Assign each of the deconstructed values.
            VisitLvalue(node.Left);
            // PROTOTYPE(NullableReferenceTypes): Handle deconstruction conversion node.Right.
            VisitRvalue(node.Right.Operand);
            SetResult(node);
            return null;
        }

        public override BoundNode VisitIncrementOperator(BoundIncrementOperator node)
        {
            Debug.Assert(!IsConditionalState);

            VisitRvalue(node.Operand);
            var operandResult = _result;
            bool setResult = false;

            if (this.State.Reachable)
            {
                // PROTOTYPE(NullableReferenceTypes): Update increment method based on operand type.
                MethodSymbol incrementOperator = (node.OperatorKind.IsUserDefined() && (object)node.MethodOpt != null && node.MethodOpt.ParameterCount == 1) ? node.MethodOpt : null;
                TypeSymbol targetTypeOfOperandConversion;

                // PROTOTYPE(NullableReferenceTypes): Update conversion method based on operand type.
                if (node.OperandConversion.IsUserDefined && (object)node.OperandConversion.Method != null && node.OperandConversion.Method.ParameterCount == 1)
                {
                    targetTypeOfOperandConversion = node.OperandConversion.Method.ReturnType.TypeSymbol;
                }
                else if ((object)incrementOperator != null)
                {
                    targetTypeOfOperandConversion = incrementOperator.Parameters[0].Type.TypeSymbol;
                }
                else
                {
                    // Either a built-in increment, or an error case.
                    targetTypeOfOperandConversion = null;
                }

                TypeSymbolWithAnnotations resultOfOperandConversionType;

                if ((object)targetTypeOfOperandConversion != null)
                {
                    // PROTOTYPE(NullableReferenceTypes): Should something special be done for targetTypeOfOperandConversion for lifted case?
                    resultOfOperandConversionType = ApplyConversion(node.Operand, node.OperandConversion, targetTypeOfOperandConversion, operandResult.Type);
                }
                else
                {
                    resultOfOperandConversionType = operandResult.Type;
                }

                TypeSymbolWithAnnotations resultOfIncrementType;
                if ((object)incrementOperator == null)
                {
                    resultOfIncrementType = resultOfOperandConversionType;
                }
                else
                {
                    ReportArgumentWarnings(node.Operand, resultOfOperandConversionType, incrementOperator.Parameters[0]);

                    resultOfIncrementType = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(incrementOperator);
                }

                resultOfIncrementType = ApplyConversion(node, node.ResultConversion, node.Type, resultOfIncrementType);

                // PROTOTYPE(NullableReferenceTypes): Check node.Type.IsErrorType() instead?
                if (!node.HasErrors)
                {
                    var op = node.OperatorKind.Operator();
                    _result = (op == UnaryOperatorKind.PrefixIncrement || op == UnaryOperatorKind.PrefixDecrement) ? resultOfIncrementType : operandResult;
                    setResult = true;

                    ReportAssignmentWarnings(node, operandResult.Type, valueType: resultOfIncrementType, useLegacyWarnings: false);
                    TrackNullableStateForAssignment(node, operandResult.Type, operandResult.Slot, valueType: resultOfIncrementType);
                }
            }

            if (!setResult)
            {
                this.SetResult(node);
            }

            return null;
        }

        public override BoundNode VisitCompoundAssignmentOperator(BoundCompoundAssignmentOperator node)
        {
            VisitLvalue(node.Left); // PROTOTYPE(NullableReferenceTypes): Method should be called VisitValue rather than VisitLvalue.
            Result left = _result;

            TypeSymbolWithAnnotations resultType;
            Debug.Assert(!IsConditionalState);

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                Result leftOnRight = GetAdjustedResult(left);
                TypeSymbolWithAnnotations leftOnRightType;

                // PROTOTYPE(NullableReferenceTypes): Update operator based on inferred argument types.
                if ((object)node.Operator.LeftType != null)
                {
                    // PROTOTYPE(NullableReferenceTypes): Ignoring top-level nullability of operator left parameter.
                    leftOnRightType = ApplyConversion(node.Left, node.LeftConversion, node.Operator.LeftType, leftOnRight.Type);
                }
                else
                {
                    leftOnRightType = null;
                }

                VisitRvalue(node.Right);
                TypeSymbolWithAnnotations rightType = _result.Type;

                if ((object)node.Operator.ReturnType != null)
                {
                    if (node.Operator.Kind.IsUserDefined() && (object)node.Operator.Method != null && node.Operator.Method.ParameterCount == 2)
                    {
                        ReportArgumentWarnings(node.Left, leftOnRightType, node.Operator.Method.Parameters[0]);
                        ReportArgumentWarnings(node.Right, rightType, node.Operator.Method.Parameters[1]);
                    }

                    resultType = InferResultNullability(node.Operator.Kind, node.Operator.Method, node.Operator.ReturnType, leftOnRightType, rightType);

                    // PROTOTYPE(NullableReferenceTypes): Ignoring top-level nullability of operator.
                    resultType = ApplyConversion(node, node.FinalConversion, node.Type, resultType);
                    ReportAssignmentWarnings(node, left.Type, resultType, useLegacyWarnings: false);
                }
                else
                {
                    resultType = TypeSymbolWithAnnotations.Create(node.Type);
                }

                TrackNullableStateForAssignment(node, left.Type, left.Slot, resultType);
                _result = resultType;
            }
            //else
            //{
            //    VisitRvalue(node.Right);
            //    AfterRightHasBeenVisited(node);
            //    resultType = null;
            //}

            return null;
        }

        public override BoundNode VisitFixedLocalCollectionInitializer(BoundFixedLocalCollectionInitializer node)
        {
            var initializer = node.Expression;
            if (initializer.Kind == BoundKind.AddressOfOperator)
            {
                initializer = ((BoundAddressOfOperator)initializer).Operand;
            }

            this.VisitRvalue(initializer);
            SetResult(node);
            return null;
        }

        public override BoundNode VisitAddressOfOperator(BoundAddressOfOperator node)
        {
            SetResult(node);
            return null;
        }

        /// <summary>
        /// Report warning passing nullable argument to non-nullable parameter
        /// (e.g.: calling `void F(string s)` with `F(maybeNull)`).
        /// </summary>
        private bool ReportNullReferenceArgumentIfNecessary(BoundExpression argument, TypeSymbolWithAnnotations argumentType, ParameterSymbol parameter, TypeSymbolWithAnnotations paramType)
        {
            if (argumentType?.IsNullable == true)
            {
                if (paramType.IsReferenceType && paramType.IsNullable == false)
                {
                    if (!ReportNullAsNonNullableReferenceIfNecessary(argument))
                    {
                        ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullReferenceArgument, argument.Syntax,
                            new FormattedSymbol(parameter, SymbolDisplayFormat.ShortFormat),
                            new FormattedSymbol(parameter.ContainingSymbol, SymbolDisplayFormat.MinimallyQualifiedFormat));
                    }
                    return true;
                }
            }
            return false;
        }

        private void ReportArgumentWarnings(BoundExpression argument, TypeSymbolWithAnnotations argumentType, ParameterSymbol parameter)
        {
            var paramType = parameter.Type;

            ReportNullReferenceArgumentIfNecessary(argument, argumentType, parameter, paramType);

            if ((object)argumentType != null && IsNullabilityMismatch(paramType.TypeSymbol, argumentType.TypeSymbol))
            {
                ReportNullabilityMismatchInArgument(argument, argumentType.TypeSymbol, parameter, paramType.TypeSymbol);
            }
        }

        /// <summary>
        /// Report warning passing argument where nested nullability does not match
        /// parameter (e.g.: calling `void F(object[] o)` with `F(new[] { maybeNull })`).
        /// </summary>
        private void ReportNullabilityMismatchInArgument(BoundExpression argument, TypeSymbol argumentType, ParameterSymbol parameter, TypeSymbol parameterType)
        {
            ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullabilityMismatchInArgument, argument.Syntax, argumentType, parameterType,
                new FormattedSymbol(parameter, SymbolDisplayFormat.ShortFormat),
                new FormattedSymbol(parameter.ContainingSymbol, SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        // PROTOTYPE(NullableReferenceTypes): If support for [NullableOptOut] or [NullableOptOutForAssembly]
        // is re-enabled, we'll need to call this helper for method symbols before inferring nullability of
        // arguments to avoid warnings when nullability checking of the method is suppressed.
        // (See all uses of this helper for method symbols.)
        private TypeSymbolWithAnnotations GetTypeOrReturnTypeWithAdjustedNullableAnnotations(Symbol symbol)
        {
            Debug.Assert(symbol.Kind != SymbolKind.Local); // Handled in VisitLocal.
            Debug.Assert(symbol.Kind != SymbolKind.Parameter); // Handled in VisitParameter.

            return compilation.GetTypeOrReturnTypeWithAdjustedNullableAnnotations(symbol);
        }

        private Result GetDeclaredLocalResult(LocalSymbol local)
        {
            var slot = GetOrCreateSlot(local);
            TypeSymbolWithAnnotations type;
            if (!_variableTypes.TryGetValue(local, out type))
            {
                type = local.Type;
            }
            return Result.Create(type, slot);
        }

        private Result GetDeclaredParameterResult(ParameterSymbol parameter)
        {
            var slot = GetOrCreateSlot(parameter);
            return Result.Create(parameter.Type, slot);
        }

        public override BoundNode VisitBaseReference(BoundBaseReference node)
        {
            VisitThisOrBaseReference(node);
            return null;
        }

        public override BoundNode VisitFieldAccess(BoundFieldAccess node)
        {
            VisitMemberAccess(node.ReceiverOpt, node.FieldSymbol, asLvalue: false);
            return null;
        }

        public override BoundNode VisitPropertyAccess(BoundPropertyAccess node)
        {
            VisitMemberAccess(node.ReceiverOpt, node.PropertySymbol, asLvalue: false);
            return null;
        }

        public override BoundNode VisitIndexerAccess(BoundIndexerAccess node)
        {
            var receiverOpt = node.ReceiverOpt;
            VisitRvalue(receiverOpt);
            CheckPossibleNullReceiver(receiverOpt);

            // PROTOTYPE(NullableReferenceTypes): Update indexer based on inferred receiver type.
            VisitArguments(node, node.Arguments, node.ArgumentRefKindsOpt, node.Indexer, node.ArgsToParamsOpt, node.Expanded);

            _result = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(node.Indexer);
            return null;
        }

        public override BoundNode VisitEventAccess(BoundEventAccess node)
        {
            VisitMemberAccess(node.ReceiverOpt, node.EventSymbol, asLvalue: false);
            return null;
        }

        private void VisitMemberAccess(BoundExpression receiverOpt, Symbol member, bool asLvalue)
        {
            Debug.Assert(!IsConditionalState);

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                Result receiverResult = VisitRvalueWithResult(receiverOpt);

                if (!member.IsStatic)
                {
                    member = AsMemberOfResultType(member);
                    CheckPossibleNullReceiver(receiverOpt);
                }

                int containingSlot = receiverResult.Slot;
                int slot = (containingSlot < 0) ? -1 : GetOrCreateSlot(member, containingSlot);
                var resultType = member.GetTypeOrReturnType();

                if (!asLvalue)
                {
                    // We are supposed to track information for the node. Use whatever we managed to
                    // accumulate so far.
                    if (resultType.IsReferenceType && slot > 0 && slot < this.State.Capacity)
                    {
                        var isNullable = !this.State[slot];
                        if (isNullable != resultType.IsNullable)
                        {
                            resultType = TypeSymbolWithAnnotations.Create(resultType.TypeSymbol, isNullable);
                        }
                    }
                }

                _result = Result.Create(resultType, slot);
            }
        }

        public override void VisitForEachIterationVariables(BoundForEachStatement node)
        {
            // declare and assign all iteration variables
            foreach (var iterationVariable in node.IterationVariables)
            {
                int slot = GetOrCreateSlot(iterationVariable);
                TypeSymbolWithAnnotations sourceType = node.EnumeratorInfoOpt?.ElementType;
                bool? isNullableIfReferenceType = null;
                if ((object)sourceType != null)
                {
                    TypeSymbolWithAnnotations destinationType = iterationVariable.Type;
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    Conversion conversion = _conversions.ClassifyImplicitConversionFromType(sourceType.TypeSymbol, destinationType.TypeSymbol, ref useSiteDiagnostics);
                    TypeSymbolWithAnnotations result = ApplyConversion(node.IterationVariableType, operandOpt: null, conversion, destinationType.TypeSymbol, sourceType, checkConversion: false, fromExplicitCast: true, out bool canConvertNestedNullability);
                    if (destinationType.IsReferenceType && destinationType.IsNullable == false && sourceType.IsNullable == true)
                    {
                        ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_ConvertingNullableToNonNullable, node.IterationVariableType.Syntax);
                    }
                    isNullableIfReferenceType = result.IsNullable;
                }
                this.State[slot] = !isNullableIfReferenceType;
            }
        }

        public override BoundNode VisitObjectInitializerMember(BoundObjectInitializerMember node)
        {
            // Should be handled by VisitObjectCreationExpression.
            throw ExceptionUtilities.Unreachable;
        }

        public override BoundNode VisitDynamicObjectInitializerMember(BoundDynamicObjectInitializerMember node)
        {
            SetResult(node);
            return null;
        }

        public override BoundNode VisitBadExpression(BoundBadExpression node)
        {
            var result = base.VisitBadExpression(node);
            _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: null);
            return result;
        }

        public override BoundNode VisitTypeExpression(BoundTypeExpression node)
        {
            var result = base.VisitTypeExpression(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitTypeOrValueExpression(BoundTypeOrValueExpression node)
        {
            var result = base.VisitTypeOrValueExpression(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitUnaryOperator(BoundUnaryOperator node)
        {
            Debug.Assert(!IsConditionalState);

            var result = base.VisitUnaryOperator(node);
            TypeSymbolWithAnnotations resultType = null;

            // PROTOTYPE(NullableReferenceTypes): Update method based on inferred operand type.
            if (node.OperatorKind.IsUserDefined())
            {
                if (node.OperatorKind.IsLifted())
                {
                    // PROTOTYPE(NullableReferenceTypes): Conversions: Lifted operator
                }
                else if ((object)node.MethodOpt != null && node.MethodOpt.ParameterCount == 1)
                {
                    ReportArgumentWarnings(node.Operand, _result.Type, node.MethodOpt.Parameters[0]);
                    resultType = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(node.MethodOpt);
                }
            }

            _result = resultType ?? TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: null);
            return null;
        }

        public override BoundNode VisitPointerIndirectionOperator(BoundPointerIndirectionOperator node)
        {
            var result = base.VisitPointerIndirectionOperator(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitPointerElementAccess(BoundPointerElementAccess node)
        {
            var result = base.VisitPointerElementAccess(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitRefTypeOperator(BoundRefTypeOperator node)
        {
            VisitRvalue(node.Operand);
            SetResult(node);
            return null;
        }

        public override BoundNode VisitMakeRefOperator(BoundMakeRefOperator node)
        {
            var result = base.VisitMakeRefOperator(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitRefValueOperator(BoundRefValueOperator node)
        {
            var result = base.VisitRefValueOperator(node);
            SetResult(node);
            return result;
        }

        private TypeSymbolWithAnnotations InferResultNullability(BoundUserDefinedConditionalLogicalOperator node)
        {
            if (node.OperatorKind.IsLifted())
            {
                // PROTOTYPE(NullableReferenceTypes): Conversions: Lifted operator
                return TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: null);
            }
            // PROTOTYPE(NullableReferenceTypes): Update method based on inferred operand types.
            if ((object)node.LogicalOperator != null && node.LogicalOperator.ParameterCount == 2)
            {
                return GetTypeOrReturnTypeWithAdjustedNullableAnnotations(node.LogicalOperator);
            }
            else
            {
                return null;
            }
        }

        protected override void AfterLeftChildOfBinaryLogicalOperatorHasBeenVisited(BoundExpression node, BoundExpression right, bool isAnd, bool isBool, ref LocalState leftTrue, ref LocalState leftFalse)
        {
            Debug.Assert(!IsConditionalState);
            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                TypeSymbolWithAnnotations leftType = _result.Type;
                // PROTOTYPE(NullableReferenceTypes): Update operator methods based on inferred operand types.
                MethodSymbol logicalOperator = null;
                MethodSymbol trueFalseOperator = null;
                BoundExpression left = null;

                switch (node.Kind)
                {
                    case BoundKind.BinaryOperator:
                        Debug.Assert(!((BoundBinaryOperator)node).OperatorKind.IsUserDefined());
                        break;
                    case BoundKind.UserDefinedConditionalLogicalOperator:
                        var binary = (BoundUserDefinedConditionalLogicalOperator)node;
                        if (binary.LogicalOperator != null && binary.LogicalOperator.ParameterCount == 2)
                        {
                            logicalOperator = binary.LogicalOperator;
                            left = binary.Left;
                            trueFalseOperator = isAnd ? binary.FalseOperator : binary.TrueOperator;

                            if ((object)trueFalseOperator != null && trueFalseOperator.ParameterCount != 1)
                            {
                                trueFalseOperator = null;
                            }
                        }
                        break;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(node.Kind);
                }

                Debug.Assert((object)trueFalseOperator == null || ((object)logicalOperator != null && left != null));

                if ((object)trueFalseOperator != null)
                {
                    ReportArgumentWarnings(left, leftType, trueFalseOperator.Parameters[0]);
                }

                if ((object)logicalOperator != null)
                {
                    ReportArgumentWarnings(left, leftType, logicalOperator.Parameters[0]);
                }

                Visit(right);
                TypeSymbolWithAnnotations rightType = _result.Type;

                _result = InferResultNullabilityOfBinaryLogicalOperator(node, leftType, rightType);

                if ((object)logicalOperator != null)
                {
                    ReportArgumentWarnings(right, rightType, logicalOperator.Parameters[1]);
                }
            }

            AfterRightChildOfBinaryLogicalOperatorHasBeenVisited(node, right, isAnd, isBool, ref leftTrue, ref leftFalse);
        }

        private TypeSymbolWithAnnotations InferResultNullabilityOfBinaryLogicalOperator(BoundExpression node, TypeSymbolWithAnnotations leftType, TypeSymbolWithAnnotations rightType)
        {
            switch (node.Kind)
            {
                case BoundKind.BinaryOperator:
                    return InferResultNullability((BoundBinaryOperator)node, leftType, rightType);
                case BoundKind.UserDefinedConditionalLogicalOperator:
                    return InferResultNullability((BoundUserDefinedConditionalLogicalOperator)node);
                default:
                    throw ExceptionUtilities.UnexpectedValue(node.Kind);
            }
        }

        public override BoundNode VisitAwaitExpression(BoundAwaitExpression node)
        {
            var result = base.VisitAwaitExpression(node);
            if (!node.Type.IsReferenceType || node.HasErrors || (object)node.GetResult == null)
            {
                SetResult(node);
            }
            else
            {
                // PROTOTYPE(NullableReferenceTypes): Update method based on inferred receiver type.
                _result = GetTypeOrReturnTypeWithAdjustedNullableAnnotations(node.GetResult);
            }
            return result;
        }

        public override BoundNode VisitTypeOfOperator(BoundTypeOfOperator node)
        {
            var result = base.VisitTypeOfOperator(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitMethodInfo(BoundMethodInfo node)
        {
            var result = base.VisitMethodInfo(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitFieldInfo(BoundFieldInfo node)
        {
            var result = base.VisitFieldInfo(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitDefaultExpression(BoundDefaultExpression node)
        {
            var result = base.VisitDefaultExpression(node);
            _result = (object)node.Type == null ?
                null :
                TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: true);
            return result;
        }

        public override BoundNode VisitIsOperator(BoundIsOperator node)
        {
            var result = base.VisitIsOperator(node);
            Debug.Assert(node.Type.SpecialType == SpecialType.System_Boolean);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitAsOperator(BoundAsOperator node)
        {
            var result = base.VisitAsOperator(node);

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                bool? isNullable = null;
                if (node.Type.IsReferenceType)
                {
                    switch (node.Conversion.Kind)
                    {
                        case ConversionKind.Identity:
                        case ConversionKind.ImplicitReference:
                            // Inherit nullability from the operand
                            isNullable = _result.Type?.IsNullable;
                            break;

                        case ConversionKind.Boxing:
                            var operandType = node.Operand.Type;
                            if (operandType?.IsValueType == true)
                            {
                                // PROTOTYPE(NullableReferenceTypes): Should we worry about a pathological case of boxing nullable value known to be not null?
                                //       For example, new int?(0)
                                isNullable = operandType.IsNullableType();
                            }
                            else
                            {
                                Debug.Assert(operandType?.IsReferenceType != true);
                                isNullable = true;
                            }
                            break;

                        default:
                            isNullable = true;
                            break;
                    }
                }
                _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: isNullable);
            }

            return result;
        }

        public override BoundNode VisitSuppressNullableWarningExpression(BoundSuppressNullableWarningExpression node)
        {
            var result = base.VisitSuppressNullableWarningExpression(node);

            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                _result = _result.Type?.SetUnknownNullabilityForReferenceTypes();
            }

            return result;
        }

        public override BoundNode VisitSizeOfOperator(BoundSizeOfOperator node)
        {
            var result = base.VisitSizeOfOperator(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitArgList(BoundArgList node)
        {
            var result = base.VisitArgList(node);
            Debug.Assert(node.Type.SpecialType == SpecialType.System_RuntimeArgumentHandle);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitArgListOperator(BoundArgListOperator node)
        {
            VisitArgumentsEvaluate(node.Arguments, node.ArgumentRefKindsOpt);
            Debug.Assert((object)node.Type == null);
            SetResult(node);
            return null;
        }

        public override BoundNode VisitLiteral(BoundLiteral node)
        {
            var result = base.VisitLiteral(node);

            Debug.Assert(!IsConditionalState);
            //if (this.State.Reachable) // PROTOTYPE(NullableReferenceTypes): Consider reachability?
            {
                var constant = node.ConstantValue;

                if (constant != null &&
                    ((object)node.Type != null ? node.Type.IsReferenceType : constant.IsNull))
                {
                    _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: constant.IsNull);
                }
                else
                {
                    SetResult(node);
                }
            }

            return result;
        }

        public override BoundNode VisitPreviousSubmissionReference(BoundPreviousSubmissionReference node)
        {
            var result = base.VisitPreviousSubmissionReference(node);
            Debug.Assert(node.WasCompilerGenerated);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitHostObjectMemberReference(BoundHostObjectMemberReference node)
        {
            var result = base.VisitHostObjectMemberReference(node);
            Debug.Assert(node.WasCompilerGenerated);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitPseudoVariable(BoundPseudoVariable node)
        {
            var result = base.VisitPseudoVariable(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitRangeVariable(BoundRangeVariable node)
        {
            var result = base.VisitRangeVariable(node);
            SetResult(node); // PROTOTYPE(NullableReferenceTypes)
            return result;
        }

        public override BoundNode VisitLabel(BoundLabel node)
        {
            var result = base.VisitLabel(node);
            SetUnknownResultNullability();
            return result;
        }

        public override BoundNode VisitDynamicMemberAccess(BoundDynamicMemberAccess node)
        {
            var receiver = node.Receiver;
            VisitRvalue(receiver);
            CheckPossibleNullReceiver(receiver);

            Debug.Assert(node.Type.IsDynamic());
            _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: null);
            return null;
        }

        public override BoundNode VisitDynamicInvocation(BoundDynamicInvocation node)
        {
            VisitRvalue(node.Expression);
            VisitArgumentsEvaluate(node.Arguments, node.ArgumentRefKindsOpt);

            Debug.Assert(node.Type.IsDynamic());
            Debug.Assert(node.Type.IsReferenceType);

            // PROTOTYPE(NullableReferenceTypes): Update applicable members based on inferred argument types.
            bool? isNullable = InferResultNullabilityFromApplicableCandidates(StaticCast<Symbol>.From(node.ApplicableMethods));
            _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: isNullable);
            return null;
        }

        public override BoundNode VisitEventAssignmentOperator(BoundEventAssignmentOperator node)
        {
            VisitRvalue(node.ReceiverOpt);
            Debug.Assert(!IsConditionalState);
            var receiverOpt = node.ReceiverOpt;
            if (!node.Event.IsStatic)
            {
                CheckPossibleNullReceiver(receiverOpt);
            }
            VisitRvalue(node.Argument);
            SetResult(node); // PROTOTYPE(NullableReferenceTypes)
            return null;
        }

        public override BoundNode VisitDynamicObjectCreationExpression(BoundDynamicObjectCreationExpression node)
        {
            Debug.Assert(!IsConditionalState);
            VisitArgumentsEvaluate(node.Arguments, node.ArgumentRefKindsOpt);
            VisitObjectOrDynamicObjectCreation(node, node.InitializerExpressionOpt);
            return null;
        }

        public override BoundNode VisitObjectInitializerExpression(BoundObjectInitializerExpression node)
        {
            // Only reachable from bad expression. Otherwise handled in VisitObjectCreationExpression().
            SetResult(node);
            return null;
        }

        public override BoundNode VisitCollectionInitializerExpression(BoundCollectionInitializerExpression node)
        {
            // Only reachable from bad expression. Otherwise handled in VisitObjectCreationExpression().
            SetResult(node);
            return null;
        }

        public override BoundNode VisitDynamicCollectionElementInitializer(BoundDynamicCollectionElementInitializer node)
        {
            // Only reachable from bad expression. Otherwise handled in VisitObjectCreationExpression().
            SetResult(node);
            return null;
        }

        public override BoundNode VisitImplicitReceiver(BoundImplicitReceiver node)
        {
            var result = base.VisitImplicitReceiver(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitAnonymousPropertyDeclaration(BoundAnonymousPropertyDeclaration node)
        {
            var result = base.VisitAnonymousPropertyDeclaration(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitNoPiaObjectCreationExpression(BoundNoPiaObjectCreationExpression node)
        {
            var result = base.VisitNoPiaObjectCreationExpression(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitNewT(BoundNewT node)
        {
            var result = base.VisitNewT(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitArrayInitialization(BoundArrayInitialization node)
        {
            var result = base.VisitArrayInitialization(node);
            SetResult(node);
            return result;
        }

        private void SetUnknownResultNullability()
        {
            _result = Result.Unset;
        }

        public override BoundNode VisitStackAllocArrayCreation(BoundStackAllocArrayCreation node)
        {
            var result = base.VisitStackAllocArrayCreation(node);
            Debug.Assert((object)node.Type == null || node.Type.IsPointerType() || node.Type.IsByRefLikeType);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitDynamicIndexerAccess(BoundDynamicIndexerAccess node)
        {
            var receiver = node.ReceiverOpt;
            VisitRvalue(receiver);
            CheckPossibleNullReceiver(receiver);
            VisitArgumentsEvaluate(node.Arguments, node.ArgumentRefKindsOpt);

            Debug.Assert(node.Type.IsDynamic());

            // PROTOTYPE(NullableReferenceTypes): Update applicable members based on inferred argument types.
            bool? isNullable = (node.Type?.IsReferenceType == true) ?
                InferResultNullabilityFromApplicableCandidates(StaticCast<Symbol>.From(node.ApplicableIndexers)) :
                null;
            _result = TypeSymbolWithAnnotations.Create(node.Type, isNullableIfReferenceType: isNullable);
            return null;
        }

        private void CheckPossibleNullReceiver(BoundExpression receiverOpt, bool checkType = true)
        {
            if (receiverOpt != null && this.State.Reachable)
            {
#if DEBUG
                Debug.Assert(receiverOpt.Type is null || _result.Type?.TypeSymbol is null || AreCloseEnough(receiverOpt.Type, _result.Type.TypeSymbol));
#endif
                TypeSymbol receiverType = receiverOpt.Type ?? _result.Type?.TypeSymbol;
                if ((object)receiverType != null &&
                    (!checkType || receiverType.IsReferenceType || receiverType.IsUnconstrainedTypeParameter()) &&
                    (_result.Type?.IsNullable == true || receiverType.IsUnconstrainedTypeParameter()))
                {
                    ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullReferenceReceiver, receiverOpt.Syntax);
                }
            }
        }

        /// <summary>
        /// Report warning converting null literal to non-nullable reference type.
        /// target (e.g.: `object x = null;` or calling `void F(object y)` with `F(null)`).
        /// </summary>
        private bool ReportNullAsNonNullableReferenceIfNecessary(BoundExpression value)
        {
            if (value.ConstantValue?.IsNull != true && !IsDefaultOfUnconstrainedTypeParameter(value))
            {
                return false;
            }
            ReportStaticNullCheckingDiagnostics(ErrorCode.WRN_NullAsNonNullable, value.Syntax);
            return true;
        }

        private static bool IsDefaultOfUnconstrainedTypeParameter(BoundExpression expr)
        {
            switch (expr.Kind)
            {
                case BoundKind.Conversion:
                    {
                        var conversion = (BoundConversion)expr;
                        // PROTOTYPE(NullableReferenceTypes): Check target type is unconstrained type parameter?
                        return conversion.Conversion.Kind == ConversionKind.DefaultOrNullLiteral &&
                            IsDefaultOfUnconstrainedTypeParameter(conversion.Operand);
                    }
                case BoundKind.DefaultExpression:
                    return IsUnconstrainedTypeParameter(expr.Type);
                default:
                    return false;
            }
        }

        private static bool IsNullabilityMismatch(TypeSymbolWithAnnotations type1, TypeSymbolWithAnnotations type2)
        {
            return type1.Equals(type2, TypeCompareKind.AllIgnoreOptions) &&
                !type1.Equals(type2, TypeCompareKind.AllIgnoreOptions | TypeCompareKind.CompareNullableModifiersForReferenceTypes | TypeCompareKind.UnknownNullableModifierMatchesAny);
        }

        private static bool IsNullabilityMismatch(TypeSymbol type1, TypeSymbol type2)
        {
            return type1.Equals(type2, TypeCompareKind.AllIgnoreOptions) &&
                !type1.Equals(type2, TypeCompareKind.AllIgnoreOptions | TypeCompareKind.CompareNullableModifiersForReferenceTypes | TypeCompareKind.UnknownNullableModifierMatchesAny);
        }

        private bool? InferResultNullabilityFromApplicableCandidates(ImmutableArray<Symbol> applicableMembers)
        {
            if (applicableMembers.IsDefaultOrEmpty)
            {
                return null;
            }

            bool? resultIsNullable = false;

            foreach (Symbol member in applicableMembers)
            {
                TypeSymbolWithAnnotations type = member.GetTypeOrReturnType();

                if (type.IsReferenceType)
                {
                    bool? memberResultIsNullable = type.IsNullable;
                    if (memberResultIsNullable == true)
                    {
                        // At least one candidate can produce null, assume dynamic access can produce null as well
                        resultIsNullable = true;
                        break;
                    }
                    else if (memberResultIsNullable == null)
                    {
                        // At least one candidate can produce result of an unknown nullability.
                        // At best, dynamic access can produce result of an unknown nullability as well.
                        resultIsNullable = null;
                    }
                }
                else if (!type.IsValueType)
                {
                    resultIsNullable = null;
                }
            }

            return resultIsNullable;
        }

        public override BoundNode VisitQueryClause(BoundQueryClause node)
        {
            var result = base.VisitQueryClause(node);
            SetResult(node); // PROTOTYPE(NullableReferenceTypes)
            return result;
        }

        public override BoundNode VisitNameOfOperator(BoundNameOfOperator node)
        {
            var result = base.VisitNameOfOperator(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitNamespaceExpression(BoundNamespaceExpression node)
        {
            var result = base.VisitNamespaceExpression(node);
            SetUnknownResultNullability();
            return result;
        }

        public override BoundNode VisitInterpolatedString(BoundInterpolatedString node)
        {
            var result = base.VisitInterpolatedString(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitStringInsert(BoundStringInsert node)
        {
            var result = base.VisitStringInsert(node);
            SetUnknownResultNullability();
            return result;
        }

        public override BoundNode VisitConvertedStackAllocExpression(BoundConvertedStackAllocExpression node)
        {
            var result = base.VisitConvertedStackAllocExpression(node);
            SetResult(node);
            return result;
        }

        public override BoundNode VisitDiscardExpression(BoundDiscardExpression node)
        {
            SetResult(node);
            return null;
        }

        public override BoundNode VisitThrowExpression(BoundThrowExpression node)
        {
            var result = base.VisitThrowExpression(node);
            SetUnknownResultNullability();
            return result;
        }

        #endregion Visitors

        protected override string Dump(LocalState state)
        {
            return string.Empty;
        }

        protected override void UnionWith(ref LocalState self, ref LocalState other)
        {
            if (self.Capacity != other.Capacity)
            {
                Normalize(ref self);
                Normalize(ref other);
            }

            for (int slot = 1; slot < self.Capacity; slot++)
            {
                bool? selfSlotIsNotNull = self[slot];
                bool? union = selfSlotIsNotNull | other[slot];
                if (selfSlotIsNotNull != union)
                {
                    self[slot] = union;
                }
            }
        }

        protected override bool IntersectWith(ref LocalState self, ref LocalState other)
        {
            if (self.Reachable == other.Reachable)
            {
                bool result = false;

                if (self.Capacity != other.Capacity)
                {
                    Normalize(ref self);
                    Normalize(ref other);
                }

                for (int slot = 1; slot < self.Capacity; slot++)
                {
                    bool? selfSlotIsNotNull = self[slot];
                    bool? intersection = selfSlotIsNotNull & other[slot];
                    if (selfSlotIsNotNull != intersection)
                    {
                        self[slot] = intersection;
                        result = true;
                    }
                }

                return result;
            }
            else if (!self.Reachable)
            {
                self = other.Clone();
                return true;
            }
            else
            {
                Debug.Assert(!other.Reachable);
                return false;
            }
        }

        [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
        internal struct Result
        {
            internal readonly TypeSymbolWithAnnotations Type;
            internal readonly int Slot;

            internal static readonly Result Unset = new Result(type: null, slot: -1);

            internal static Result Create(TypeSymbolWithAnnotations type, int slot = -1)
            {
                return new Result(type, slot);
            }

            // PROTOTYPE(NullableReferenceTypes): Consider replacing implicit cast operators with
            // explicit methods - either Result.Create() overloads or ToResult() extension methods.
            public static implicit operator Result(TypeSymbolWithAnnotations type)
            {
                return Result.Create(type);
            }

            private Result(TypeSymbolWithAnnotations type, int slot)
            {
                Type = type;
                Slot = slot;
            }

            private string GetDebuggerDisplay()
            {
                var type = (object)Type == null ? "<null>" : Type.GetDebuggerDisplay();
                return $"Type={type}, Slot={Slot}";
            }
        }

        [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
#if REFERENCE_STATE
        internal class LocalState : AbstractLocalState
#else
        internal struct LocalState : AbstractLocalState
#endif
        {
            // PROTOTYPE(NullableReferenceTypes): Consider storing nullability rather than non-nullability
            // or perhaps expose as nullability from `this[int]` even if stored differently.
            private BitVector _knownNullState; // No diagnostics should be derived from a variable with a bit set to 0.
            private BitVector _notNull;

            internal LocalState(BitVector knownNullState, BitVector notNull)
            {
                Debug.Assert(!knownNullState.IsNull);
                Debug.Assert(!notNull.IsNull);
                this._knownNullState = knownNullState;
                this._notNull = notNull;
            }

            internal int Capacity => _knownNullState.Capacity;

            internal void EnsureCapacity(int capacity)
            {
                _knownNullState.EnsureCapacity(capacity);
                _notNull.EnsureCapacity(capacity);
            }

            internal bool? this[int slot]
            {
                get
                {
                    return _knownNullState[slot] ? _notNull[slot] : (bool?)null;
                }
                set
                {
                    _knownNullState[slot] = value.HasValue;
                    _notNull[slot] = value.GetValueOrDefault();
                }
            }

            /// <summary>
            /// Produce a duplicate of this flow analysis state.
            /// </summary>
            /// <returns></returns>
            public LocalState Clone()
            {
                return new LocalState(_knownNullState.Clone(), _notNull.Clone());
            }

            public bool Reachable
            {
                get
                {
                    return _knownNullState.Capacity > 0;
                }
            }

            internal string GetDebuggerDisplay()
            {
                var pooledBuilder = PooledStringBuilder.GetInstance();
                var builder = pooledBuilder.Builder;
                builder.Append(" ");
                for (int i = this.Capacity - 1; i >= 0; i--)
                {
                    builder.Append(_knownNullState[i] ? (_notNull[i] ? "!" : "?") : "_");
                }

                return pooledBuilder.ToStringAndFree();
            }
        }
    }
}
