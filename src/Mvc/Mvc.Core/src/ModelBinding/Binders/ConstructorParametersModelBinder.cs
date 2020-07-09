// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Mvc.ModelBinding.Binders
{
    /// <summary>
    /// <see cref="IModelBinder"/> implementation for binding complex types.
    /// </summary>
    public class ConstructorParametersModelBinder : IModelBinder
    {
        // Don't want a new public enum because communication between the private and internal methods of this class
        // should not be exposed. Can't use an internal enum because types of [TheoryData] values must be public.

        // Model contains only parameters that are expected to bind from value providers and no value provider has
        // matching data.
        internal const int NoDataAvailable = 0;
        // If model contains parameters that are expected to bind from value providers, no value provider has matching
        // data. Remaining (greedy) parameters might bind successfully.
        internal const int GreedyParametersMayHaveData = 1;
        // Model contains at least one parameter that is expected to bind from value providers and a value provider has
        // matching data.
        internal const int ValueProviderDataAvailable = 2;
        
        private readonly ModelMetadata _boundConstructor;
        private readonly IReadOnlyList<IModelBinder> _parameterBinders;
        private readonly ILogger _logger;

        internal ConstructorParametersModelBinder(
            ModelMetadata boundConstructor,
            IReadOnlyList<IModelBinder> parameterBinders,
            ILogger<ConstructorParametersModelBinder> logger)
        {
            _boundConstructor = boundConstructor;
            _parameterBinders = parameterBinders;
            _logger = logger;
        }

        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            if (bindingContext == null)
            {
                throw new ArgumentNullException(nameof(bindingContext));
            }

            _logger.AttemptingToBindModel(bindingContext);

            var parameterData = CanCreateModel(bindingContext);
            if (parameterData == NoDataAvailable)
            {
                return Task.CompletedTask;
            }

            // Perf: separated to avoid allocating a state machine when we don't
            // need to go async.
            return BindModelCoreAsync(bindingContext, parameterData);
        }

        private async Task BindModelCoreAsync(ModelBindingContext bindingContext, int parameterData)
        {
            Debug.Assert(parameterData == GreedyParametersMayHaveData || parameterData == ValueProviderDataAvailable);

            var attemptedBinding = false;
            var postponePlaceholderBinding = false;
            var parameterBindingSucceeded = false;
            var values = ParameterDefaultValues.GetParameterDefaultValues(_boundConstructor.Identity.ConstructorInfo);

            for (var i = 0; i < _boundConstructor.Parameters.Count; i++)
            {
                var parameter = _boundConstructor.Parameters[i];
                if (!CanBindParameter(bindingContext, parameter))
                {
                    continue;
                }

                var binder = _parameterBinders[i];

                if (binder is PlaceholderBinder)
                {
                    if (postponePlaceholderBinding)
                    {
                        // Decided to postpone binding parameters that complete a loop in the model types when handling
                        // an earlier loop-completing parameter. Postpone binding this parameter too.
                        continue;
                    }
                    else if (!bindingContext.IsTopLevelObject &&
                        !parameterBindingSucceeded &&
                        parameterData == GreedyParametersMayHaveData)
                    {
                        // Have no confirmation of data for the current instance. Postpone completing the loop until
                        // we _know_ the current instance is useful. Recursion would otherwise occur prior to the
                        // block with a similar condition after the loop.
                        //
                        // Example cases include an Employee class containing
                        // 1. a Manager parameter of type Employee
                        // 2. an Employees parameter of type IList<Employee>
                        postponePlaceholderBinding = true;
                        continue;
                    }
                }

                var fieldName = parameter.BinderModelName ?? parameter.ParameterName;
                var modelName = ModelNames.CreatePropertyModelName(bindingContext.ModelName, fieldName);
                var result = await BindParameter(bindingContext, parameter, binder, fieldName, modelName);

                if (result.IsModelSet)
                {
                    values[i] = result.Model;

                    attemptedBinding = true;
                    parameterBindingSucceeded = true;
                }
                else if (parameter.IsBindingRequired)
                {
                    attemptedBinding = true;
                }
            }

            if (postponePlaceholderBinding && parameterBindingSucceeded)
            {
                // Have some data for this instance. Continue with the model type loop.
                for (var i = 0; i < _boundConstructor.Parameters.Count; i++)
                {
                    var parameter = _boundConstructor.Parameters[i];
                    if (!CanBindParameter(bindingContext, parameter))
                    {
                        continue;
                    }

                    var binder = _parameterBinders[i];

                    if (binder is PlaceholderBinder)
                    {
                        var fieldName = parameter.BinderModelName ?? parameter.ParameterName;
                        var modelName = ModelNames.CreatePropertyModelName(bindingContext.ModelName, fieldName);
                        await BindParameter(bindingContext, parameter, binder, fieldName, modelName);
                    }
                }
            }

            // Did we violate [BindRequired] on the model? This case occurs if
            // 1. All parameters in a [BindRequired] model have [BindNever] or are otherwise excluded from binding.
            // 2. No data exists for any parameter.
            if (!attemptedBinding &&
                bindingContext.IsTopLevelObject &&
                bindingContext.ModelMetadata.IsBindingRequired)
            {
                var messageProvider = bindingContext.ModelMetadata.ModelBindingMessageProvider;
                var message = messageProvider.MissingBindRequiredValueAccessor(bindingContext.FieldName);
                bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, message);
            }
            else if (bindingContext.IsTopLevelObject || parameterBindingSucceeded)
            {
                try
                {
                    bindingContext.Model = _boundConstructor.BoundConstructorInvoker(values);
                }
                catch (Exception exception)
                {
                    AddModelError(exception, bindingContext.ModelName, bindingContext);
                    return;
                }
            }
            

            _logger.DoneAttemptingToBindModel(bindingContext);

            // Have all binders failed because no data was available?
            //
            // If CanCreateModel determined a parameter has data, failures are likely due to conversion errors. For
            // example, user may submit ?[0].id=twenty&[1].id=twenty-one&[2].id=22 for a collection of a complex type
            // with an int id parameter. In that case, the bound model should be [ {}, {}, { id = 22 }] and
            // ModelState should contain errors about both [0].id and [1].id. Do not inform higher-level binders of the
            // failure in this and similar cases.
            //
            // If CanCreateModel could not find data for non-greedy parameters, failures indicate greedy binders were
            // unsuccessful. For example, user may submit file attachments [0].File and [1].File but not [2].File for
            // a collection of a complex type containing an IFormFile parameter. In that case, we have exhausted the
            // attached files and checking for [3].File is likely be pointless. (And, if it had a point, would we stop
            // after 10 failures, 100, or more -- all adding redundant errors to ModelState?) Inform higher-level
            // binders of the failure.
            //
            // Required parameters do not change the logic below. Missed required parameters cause ModelState errors
            // but do not necessarily prevent further attempts to bind.
            //
            // This logic is intended to maximize correctness but does not avoid infinite loops or recursion when a
            // greedy model binder succeeds unconditionally.
            if (!bindingContext.IsTopLevelObject &&
                !parameterBindingSucceeded &&
                parameterData == GreedyParametersMayHaveData)
            {
                bindingContext.Result = ModelBindingResult.Failed();
                return;
            }

            bindingContext.Result = ModelBindingResult.Success(bindingContext.Model);
        }

        private async ValueTask<ModelBindingResult> BindParameter(
            ModelBindingContext bindingContext,
            ModelMetadata parameter,
            IModelBinder parameterBinder,
            string fieldName,
            string modelName)
        {
            // Pass complex (including collection) values down so that binding system does not unnecessarily
            // recreate instances or overwrite inner parameters that are not bound. No need for this with simple
            // values because they will be overwritten if binding succeeds. Arrays are never reused because they
            // cannot be resized.
            ModelBindingResult result;
            using (bindingContext.EnterNestedScope(
                modelMetadata: parameter,
                fieldName: fieldName,
                modelName: modelName,
                model: null))
            {
                await parameterBinder.BindModelAsync(bindingContext);
                result = bindingContext.Result;
            }

            if (!result.IsModelSet && parameter.IsBindingRequired)
            {
                var message = parameter.ModelBindingMessageProvider.MissingBindRequiredValueAccessor(fieldName);
                bindingContext.ModelState.TryAddModelError(modelName, message);
            }

            return result;
        }

        internal int CanCreateModel(ModelBindingContext bindingContext)
        {
            var isTopLevelObject = bindingContext.IsTopLevelObject;

            // If we get here the model is a complex object which was not directly bound by any previous model binder,
            // so we want to decide if we want to continue binding. This is important to get right to avoid infinite
            // recursion.
            //
            // First, we want to make sure this object is allowed to come from a value provider source as this binder
            // will only include value provider data. For instance if the model is marked with [FromBody], then we
            // can just skip it. A greedy source cannot be a value provider.
            //
            // If the model isn't marked with ANY binding source, then we assume it's OK also.
            //
            // We skip this check if it is a top level object because we want to always evaluate
            // the creation of top level object (this is also required for ModelBinderAttribute to work.)
            var bindingSource = bindingContext.BindingSource;
            if (!isTopLevelObject && bindingSource != null && bindingSource.IsGreedy)
            {
                return NoDataAvailable;
            }

            // Create the object if:
            // 1. It is a top level model.
            if (isTopLevelObject)
            {
                return ValueProviderDataAvailable;
            }

            // 2. Any of the model parameters can be bound.
            return CanBindAnyParameter(bindingContext);
        }

        private bool CanBindParameter(ModelBindingContext bindingContext, ModelMetadata parameterMetadata)
        {
            // Note that we use the property filter declarated on the current model rather than on the constructor.
            // MVC does not have a mechanism to specify filters on methods, but you can attribute your model with attributes such as
            // Bind to filter properties.
            var metadataProviderFilter = bindingContext.ModelMetadata.PropertyFilterProvider?.PropertyFilter;
            if (metadataProviderFilter?.Invoke(parameterMetadata) == false)
            {
                return false;
            }

            if (bindingContext.PropertyFilter?.Invoke(parameterMetadata) == false)
            {
                return false;
            }

            if (!parameterMetadata.IsBindingAllowed)
            {
                return false;
            }

            return true;
        }

        private int CanBindAnyParameter(ModelBindingContext bindingContext)
        {
            // If there are no parameters on the model, there is nothing to bind. We are here means this is not a top
            // level object. So we return false.
            if (bindingContext.ModelMetadata.Properties.Count == 0)
            {
                _logger.NoPublicSettableProperties(bindingContext);
                return NoDataAvailable;
            }

            // We want to check to see if any of the parameters of the model can be bound using the value providers or
            // a greedy binder.
            //
            // Because a parameter might specify a custom binding source ([FromForm]), it's not correct
            // for us to just try bindingContext.ValueProvider.ContainsPrefixAsync(bindingContext.ModelName);
            // that may include other value providers - that would lead us to mistakenly create the model
            // when the data is coming from a source we should use (ex: value found in query string, but the
            // model has [FromForm]).
            //
            // To do this we need to enumerate the parameters, and see which of them provide a binding source
            // through metadata, then we decide what to do.
            //
            //      If a parameter has a binding source, and it's a greedy source, then it's always bound.
            //
            //      If a parameter has a binding source, and it's a non-greedy source, then we'll filter the
            //      the value providers to just that source, and see if we can find a matching prefix
            //      (see CanBindValue).
            //
            //      If a parameter does not have a binding source, then it's fair game for any value provider.
            //
            // Bottom line, if any parameter meets the above conditions and has a value from ValueProviders, then we'll
            // create the model and try to bind it. Of, if ANY parameters of the model have a greedy source,
            // then we go ahead and create it.
            var hasGreedyBinders = false;
            for (var i = 0; i < _boundConstructor.Parameters.Count; i++)
            {
                var parameterMetadata = _boundConstructor.Parameters[i];
                if (!CanBindParameter(bindingContext, parameterMetadata))
                {
                    continue;
                }

                // If any parameter can be bound from a greedy binding source, then success.
                var bindingSource = parameterMetadata.BindingSource;
                if (bindingSource != null && bindingSource.IsGreedy)
                {
                    hasGreedyBinders = true;
                    continue;
                }

                // Otherwise, check whether the (perhaps filtered) value providers have a match.
                var fieldName = parameterMetadata.BinderModelName ?? parameterMetadata.ParameterName;
                var modelName = ModelNames.CreatePropertyModelName(bindingContext.ModelName, fieldName);
                using (bindingContext.EnterNestedScope(
                    modelMetadata: parameterMetadata,
                    fieldName: fieldName,
                    modelName: modelName,
                    model: null))
                {
                    // If any parameter can be bound from a value provider, then success.
                    if (bindingContext.ValueProvider.ContainsPrefix(bindingContext.ModelName))
                    {
                        return ValueProviderDataAvailable;
                    }
                }
            }

            if (hasGreedyBinders)
            {
                return GreedyParametersMayHaveData;
            }

            _logger.CannotBindToComplexType(bindingContext);

            return NoDataAvailable;
        }

        private static void AddModelError(
            Exception exception,
            string modelName,
            ModelBindingContext bindingContext)
        {
            var targetInvocationException = exception as TargetInvocationException;
            if (targetInvocationException?.InnerException != null)
            {
                exception = targetInvocationException.InnerException;
            }

            // Do not add an error message if a binding error has already occurred for this parameter.
            var modelState = bindingContext.ModelState;
            var validationState = modelState.GetFieldValidationState(modelName);
            if (validationState == ModelValidationState.Unvalidated)
            {
                modelState.AddModelError(modelName, exception, bindingContext.ModelMetadata);
            }
        }
    }
}
