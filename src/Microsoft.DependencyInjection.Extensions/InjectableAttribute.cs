using Microsoft.Extensions.DependencyInjection;
using System;

namespace Microsoft.DependencyInjection.Extensions
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class InjectableAttribute : Attribute
    {
        public ServiceLifetime Lifetime { get; }

        public InjectableAttribute(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        {
            Lifetime = lifetime;
        }
    }
}