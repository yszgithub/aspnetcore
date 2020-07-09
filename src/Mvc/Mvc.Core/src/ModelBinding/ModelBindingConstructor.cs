using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Mvc.ModelBinding
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
    public sealed class ModelBindingConstructor : Attribute
    {
    }
}
