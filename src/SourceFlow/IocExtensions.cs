using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow
{
    public static class IocExtensions
    {
        public static IServiceCollection UseSourceFlow(this IServiceCollection services)
        {
            services.AddAsTypesOfInterface<IAggregateFactory>(ServiceLifetime.Singleton);
            services.AddAsTypesOfInterface<IAggregateRepository>(ServiceLifetime.Singleton);
            services.AddAsTypesOfInterface<IEventStore>(ServiceLifetime.Singleton);

            //services.AddAsTypesOfInterface<IAggregateRoot>(ServiceLifetime.Transient);
            services.AddAsImplementedSubclassAndInterfacesOf<BaseCommandService>(ServiceLifetime.Singleton);

            services.AddSingleton<ICommandBus, CommandBus>(c => new CommandBus(
                c.GetService<IEventStore>(),
                c.GetService<IAggregateFactory>(),
                c.GetServices<ISagaHandler>()));

            // return new SourceFlowConfig { Services = services };
            return services;
        }

        public static IServiceCollection WithSaga<T, TSaga>(this IServiceCollection services, Func<IServiceProvider, ISaga<T>> sagaRegister)
        where T : IAggregateRoot
        where TSaga : class, ISaga<T>
        {
            if (sagaRegister == null)
                throw new ArgumentNullException(nameof(sagaRegister));

            services.AddSingleton<ISagaHandler, TSaga>(c =>
            {
                var saga = sagaRegister(c);
                if (saga == null)
                    throw new InvalidOperationException($"Saga registration for {typeof(T).Name} returned null.");

                //var busRegister = c.GetService<IBusSagaRegister>();

                //busRegister.RegisterSaga(saga);

                return (TSaga)saga;
            });
            return services;
        }
    }

    public class SourceFlowConfig
    {
        public IServiceCollection Services { get; set; }

        //private Func<IServiceProvider, ISaga<T>> sagaRegister
    }
}