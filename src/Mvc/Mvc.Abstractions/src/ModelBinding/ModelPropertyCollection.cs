// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Microsoft.AspNetCore.Mvc.ModelBinding
{
    /// <summary>
    /// A read-only collection of <see cref="ModelMetadata"/> objects which represent model properties.
    /// </summary>
    public class ModelPropertyCollection : ReadOnlyCollection<ModelMetadata>
    {
        /// <summary>
        /// Creates a new <see cref="ModelPropertyCollection"/>.
        /// </summary>
        /// <param name="properties">The properties.</param>
        public ModelPropertyCollection(IEnumerable<ModelMetadata> properties)
            : base(properties.ToList())
        {
        }

        /// <summary>
        /// Gets a <see cref="ModelMetadata"/> instance for the property corresponding to <paramref name="propertyName"/>.
        /// </summary>
        /// <param name="propertyName">
        /// The property name. Property names are compared using <see cref="StringComparison.Ordinal"/>.
        /// </param>
        /// <returns>
        /// The <see cref="ModelMetadata"/> instance for the property specified by <paramref name="propertyName"/>, or
        /// <c>null</c> if no match can be found.
        /// </returns>
        public ModelMetadata? this[string propertyName]
        {
            get
            {
                if (propertyName == null)
                {
                    throw new ArgumentNullException(nameof(propertyName));
                }

                for (var i = 0; i < Items.Count; i++)
                {
                    var property = Items[i];
                    if (string.Equals(property.PropertyName, propertyName, StringComparison.Ordinal))
                    {
                        return property;
                    }
                }

                return null;
            }
        }
    }

    public sealed class ModelParameterCollection : ReadOnlyCollection<ModelMetadata>
    {
        /// <summary>
        /// Creates a new <see cref="ModelPropertyCollection"/>.
        /// </summary>
        /// <param name="parameters">The parameters.</param>
        public ModelParameterCollection(IEnumerable<ModelMetadata> parameters)
            : base(parameters.ToList())
        {
        }

        /// <summary>
        /// Gets a <see cref="ModelMetadata"/> instance for the parameter corresponding to <paramref name="parameterName"/>.
        /// </summary>
        /// <param name="parameterName">
        /// The parameter name. Parameter names are compared using <see cref="StringComparison.Ordinal"/>.
        /// </param>
        /// <returns>
        /// The <see cref="ModelMetadata"/> instance for the parameter specified by <paramref name="parameterName"/>, or
        /// <c>null</c> if no match can be found.
        /// </returns>
        public ModelMetadata? this[string parameterName]
        {
            get
            {
                if (parameterName == null)
                {
                    throw new ArgumentNullException(nameof(parameterName));
                }

                for (var i = 0; i < Items.Count; i++)
                {
                    var parameter = Items[i];
                    if (string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal))
                    {
                        return parameter;
                    }
                }

                return null;
            }
        }
    }
}