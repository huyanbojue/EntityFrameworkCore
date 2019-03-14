﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public static class NavigationExpansionHelpers
    {
        public static (Expression source, ParameterExpression parameter) AddNavigationJoin(
            Expression sourceExpression,
            ParameterExpression parameterExpression,
            SourceMapping sourceMapping,
            NavigationTreeNode navigationTree,
            NavigationExpansionExpressionState state,
            List<INavigation> navigationPath,
            bool include)
        {
            var joinNeeded = include
                ? navigationTree.Included == NavigationTreeNodeIncludeMode.ReferencePending
                : navigationTree.ExpansionMode == NavigationTreeNodeExpansionMode.ReferencePending;

            if (joinNeeded)
            {
                var navigation = navigationTree.Navigation;
                var sourceType = sourceExpression.Type.GetSequenceType();
                var navigationTargetEntityType = navigation.GetTargetType();

                var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(navigationTargetEntityType.ClrType);
                var resultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, navigationTargetEntityType.ClrType);

                var outerParameter = Expression.Parameter(sourceType, parameterExpression.Name);
                var outerKeySelectorParameter = outerParameter;
                var transparentIdentifierAccessorExpression = BuildTransparentIdentifierAccessorExpression(outerParameter, navigationTree.Parent.ToMapping);

                var outerKeySelectorBody = CreateKeyAccessExpression(
                    transparentIdentifierAccessorExpression,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.Properties
                        : navigation.ForeignKey.PrincipalKey.Properties,
                    addNullCheck: navigationTree.Parent != null && navigationTree.Parent.Optional);

                var innerKeySelectorParameterType = navigationTargetEntityType.ClrType;
                var innerKeySelectorParameter = Expression.Parameter(
                    innerKeySelectorParameterType,
                    parameterExpression.Name + "." + navigationTree.Navigation.Name);

                var innerKeySelectorBody = CreateKeyAccessExpression(
                    innerKeySelectorParameter,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.PrincipalKey.Properties
                        : navigation.ForeignKey.Properties);

                if (outerKeySelectorBody.Type.IsNullableType()
                    && !innerKeySelectorBody.Type.IsNullableType())
                {
                    innerKeySelectorBody = Expression.Convert(innerKeySelectorBody, outerKeySelectorBody.Type);
                }
                else if (innerKeySelectorBody.Type.IsNullableType()
                    && !outerKeySelectorBody.Type.IsNullableType())
                {
                    outerKeySelectorBody = Expression.Convert(outerKeySelectorBody, innerKeySelectorBody.Type);
                }

                var outerKeySelector = Expression.Lambda(
                    outerKeySelectorBody,
                    outerKeySelectorParameter);

                var innerKeySelector = Expression.Lambda(
                    innerKeySelectorBody,
                    innerKeySelectorParameter);

                var oldParameterExpression = parameterExpression;
                if (navigationTree.Optional)
                {
                    var groupingType = typeof(IEnumerable<>).MakeGenericType(navigationTargetEntityType.ClrType);
                    var groupJoinResultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, groupingType);

                    var groupJoinMethodInfo = LinqMethodHelpers.QueryableGroupJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        groupJoinResultType);

                    // TODO: massive hack!!!!
                    if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        groupJoinMethodInfo = LinqMethodHelpers.EnumerableGroupJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        groupJoinResultType);
                    }

                    var resultSelectorOuterParameterName = outerParameter.Name;
                    var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

                    var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
                    var resultSelectorInnerParameter = Expression.Parameter(groupingType, resultSelectorInnerParameterName);

                    var groupJoinResultTransparentIdentifierCtorInfo
                        = groupJoinResultType.GetTypeInfo().GetConstructors().Single();

                    var groupJoinResultSelector = Expression.Lambda(
                        Expression.New(groupJoinResultTransparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
                        resultSelectorOuterParameter,
                        resultSelectorInnerParameter);

                    var groupJoinMethodCall
                        = Expression.Call(
                            groupJoinMethodInfo,
                            sourceExpression,
                            entityQueryable,
                            outerKeySelector,
                            innerKeySelector,
                            groupJoinResultSelector);

                    var selectManyResultType = typeof(TransparentIdentifier<,>).MakeGenericType(groupJoinResultType, navigationTargetEntityType.ClrType);

                    var selectManyMethodInfo = LinqMethodHelpers.QueryableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
                        groupJoinResultType,
                        navigationTargetEntityType.ClrType,
                        selectManyResultType);

                    // TODO: massive hack!!!!
                    if (groupJoinMethodCall.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || groupJoinMethodCall.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        selectManyMethodInfo = LinqMethodHelpers.EnumerableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
                            groupJoinResultType,
                            navigationTargetEntityType.ClrType,
                            selectManyResultType);
                    }

                    var defaultIfEmptyMethodInfo = LinqMethodHelpers.EnumerableDefaultIfEmptyMethodInfo.MakeGenericMethod(navigationTargetEntityType.ClrType);

                    var selectManyCollectionSelectorParameter = Expression.Parameter(groupJoinResultType);
                    var selectManyCollectionSelector = Expression.Lambda(
                        Expression.Call(
                            defaultIfEmptyMethodInfo,
                            Expression.Field(selectManyCollectionSelectorParameter, nameof(TransparentIdentifier<object, object>.Inner))),
                        selectManyCollectionSelectorParameter);

                    var selectManyResultTransparentIdentifierCtorInfo
                        = selectManyResultType.GetTypeInfo().GetConstructors().Single();

                    // TODO: dont reuse parameters here?
                    var selectManyResultSelector = Expression.Lambda(
                        Expression.New(selectManyResultTransparentIdentifierCtorInfo, selectManyCollectionSelectorParameter, innerKeySelectorParameter),
                        selectManyCollectionSelectorParameter,
                        innerKeySelectorParameter);

                    var selectManyMethodCall
                        = Expression.Call(selectManyMethodInfo,
                        groupJoinMethodCall,
                        selectManyCollectionSelector,
                        selectManyResultSelector);

                    sourceType = selectManyResultSelector.ReturnType;
                    sourceExpression = selectManyMethodCall;

                    var transparentIdentifierParameterName = resultSelectorInnerParameterName;
                    var transparentIdentifierParameter = Expression.Parameter(selectManyResultSelector.ReturnType, transparentIdentifierParameterName);
                    parameterExpression = transparentIdentifierParameter;
                }
                else
                {
                    var joinMethodInfo = LinqMethodHelpers.QueryableJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        resultType);

                    // TODO: massive hack!!!!
                    if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        joinMethodInfo = LinqMethodHelpers.EnumerableJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        resultType);
                    }

                    var resultSelectorOuterParameterName = outerParameter.Name;
                    var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

                    var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
                    var resultSelectorInnerParameter = Expression.Parameter(navigationTargetEntityType.ClrType, resultSelectorInnerParameterName);

                    var transparentIdentifierCtorInfo
                        = resultType.GetTypeInfo().GetConstructors().Single();

                    var resultSelector = Expression.Lambda(
                        Expression.New(transparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
                        resultSelectorOuterParameter,
                        resultSelectorInnerParameter);

                    var joinMethodCall = Expression.Call(
                        joinMethodInfo,
                        sourceExpression,
                        entityQueryable,
                        outerKeySelector,
                        innerKeySelector,
                        resultSelector);

                    sourceType = resultSelector.ReturnType;
                    sourceExpression = joinMethodCall;

                    var transparentIdentifierParameterName = resultSelectorInnerParameterName;
                    var transparentIdentifierParameter = Expression.Parameter(resultSelector.ReturnType, transparentIdentifierParameterName);
                    parameterExpression = transparentIdentifierParameter;
                }

                // remap navigation 'To' paths -> for this navigation prepend "Inner", for every other (already expanded) navigation prepend "Outer"
                navigationTree.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Inner));
                foreach (var mapping in state.SourceMappings)
                {
                    // TODO: this is very hacky - combine those two enums
                    var nodes = include
                        ? mapping.NavigationTree.Flatten().Where(n => (n.Included == NavigationTreeNodeIncludeMode.ReferenceComplete || n.ExpansionMode == NavigationTreeNodeExpansionMode.ReferenceComplete) && n != navigationTree)
                        : mapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.ReferenceComplete && n != navigationTree);

                    foreach (var navigationTreeNode in nodes)
                    {
                        navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                        if (navigationTree.Optional)
                        {
                            navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                        }
                    }
                }

                foreach (var customRootMapping in state.CustomRootMappings)
                {
                    customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                    if (navigationTree.Optional)
                    {
                        customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                    }
                }

                if (include)
                {
                    navigationTree.Included = NavigationTreeNodeIncludeMode.ReferenceComplete;
                }
                else
                {
                    navigationTree.ExpansionMode = NavigationTreeNodeExpansionMode.ReferenceComplete;

                }
                navigationPath.Add(navigation);
            }
            else
            {
                navigationPath.Add(navigationTree.Navigation);
            }

            var result = (source: sourceExpression, parameter: parameterExpression);
            foreach (var child in navigationTree.Children.Where(n => !n.Navigation.IsCollection()))
            {
                result = AddNavigationJoin(
                    result.source,
                    result.parameter,
                    sourceMapping,
                    child,
                    state,
                    navigationPath.ToList(),
                    include);
            }

            return result;
        }

        public static Expression BuildTransparentIdentifierAccessorExpression(
            [NotNull] Expression source,
            [NotNull] List<string> accessorPath)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(accessorPath, nameof(accessorPath));

            var result = source;
            foreach (var accessorPathElement in accessorPath)
            {
                result = Expression.PropertyOrField(result, accessorPathElement);
            }

            return result;
        }

        public static Expression CreateKeyAccessExpression(
            Expression target, IReadOnlyList<IProperty> properties, bool addNullCheck = false)
            => properties.Count == 1
                ? CreatePropertyExpression(target, properties[0], addNullCheck)
                : Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        properties
                            .Select(p => Expression.Convert(CreatePropertyExpression(target, p, addNullCheck), typeof(object)))
                            .Cast<Expression>()
                            .ToArray()));

        private static Expression CreatePropertyExpression(Expression target, IProperty property, bool addNullCheck)
        {
            var propertyExpression = target.CreateEFPropertyExpression(property, makeNullable: false);

            var propertyDeclaringType = property.DeclaringType.ClrType;
            if (propertyDeclaringType != target.Type
                && target.Type.GetTypeInfo().IsAssignableFrom(propertyDeclaringType.GetTypeInfo()))
            {
                if (!propertyExpression.Type.IsNullableType())
                {
                    propertyExpression = Expression.Convert(propertyExpression, propertyExpression.Type.MakeNullable());
                }

                return Expression.Condition(
                    Expression.TypeIs(target, propertyDeclaringType),
                    propertyExpression,
                    Expression.Constant(null, propertyExpression.Type));
            }

            return addNullCheck
                ? new NullConditionalExpression(target, propertyExpression)
                : propertyExpression;
        }

        public static Expression CreateNullKeyExpression(Type resultType, int keyCount)
            => resultType == typeof(AnonymousObject)
                ? Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        Enumerable.Repeat(
                            Expression.Constant(null),
                            keyCount)))
                : (Expression)Expression.Constant(null);

        // TODO find better place for this
        public static readonly MethodInfo MaterializeCollectionNavigationMethodInfo
            = typeof(NavigationExpansionHelpers).GetTypeInfo()
                .GetDeclaredMethod(nameof(MaterializeCollectionNavigation));

        public static TResult MaterializeCollectionNavigation<TResult, TEntity>(
            IEnumerable<object> elements,
            INavigation navigation)
            //where TResult: ICollection<TEntity>
            where TResult : IEnumerable<TEntity>
        {
            var collection = navigation.GetCollectionAccessor().Create(elements);

            return (TResult)collection;
        }
    }
}
