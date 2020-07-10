// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Mvc.ModelBinding.Binders
{
    /// <summary>
    /// An <see cref="IModelBinderProvider"/> for complex types.
    /// </summary>
    public class ConstructorParametersModelBinderProvider : IModelBinderProvider
    {
        /// <inheritdoc />
        public IModelBinder GetBinder(ModelBinderProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var metadata = context.Metadata;
            if (metadata.IsComplexType && !metadata.IsCollectionType && metadata.BoundConstructor is ModelMetadata boundConstructor)
            {
                var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
                var parameterBinders = new IModelBinder[boundConstructor.Parameters.Count];

                for (var i = 0; i < parameterBinders.Length; i++)
                {
                    parameterBinders[i] = context.CreateBinder(boundConstructor.Parameters[i]);
                }

                return new DefaultModelBinder(boundConstructor, parameterBinders, loggerFactory.CreateLogger<DefaultModelBinder>());
            }

            return null;
        }
    }
}
