﻿using Fluxor.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Fluxor.Modularlization
{
	internal class ModuleLoader : IModuleLoader
	{
		private readonly IObjectBuilder ObjectBuilder;
		private readonly object SyncRoot = new object();

		public ModuleLoader(IObjectBuilder objectBuilder)
		{
			ObjectBuilder = objectBuilder ?? throw new ArgumentNullException(nameof(objectBuilder));
		}

		public void Load(
			IStore store,
			IEnumerable<Assembly> assembliesToScan,
			IEnumerable<Type> middlewareTypes)
		{
			if (assembliesToScan == null)
				throw new ArgumentNullException(nameof(assembliesToScan));
			if (!assembliesToScan.Any())
				throw new ArgumentException("At least one assembly is required", nameof(assembliesToScan));

			middlewareTypes = middlewareTypes ?? Array.Empty<Type>();
			assembliesToScan = assembliesToScan.Distinct().ToArray();
			middlewareTypes = middlewareTypes.Distinct().ToArray();

			lock (SyncRoot)
			{
				DependencyScanner.Scan(
					assembliesToScan,
					middlewareTypes,
					out DiscoveredFeatureClass[] discoveredFeatureClasses,
					out DiscoveredReducerClass[] discoveredReducerClasses,
					out DiscoveredReducerMethod[] discoveredReducerMethods,
					out DiscoveredEffectClass[] discoveredEffectClasses,
					out DiscoveredEffectMethod[] discoveredEffectMethods,
					out DiscoveredMiddleware[] discoveredMiddlewares);

				FixUpStore(
					store,
					discoveredFeatureClasses,
					discoveredReducerClasses,
					discoveredReducerMethods,
					discoveredEffectClasses,
					discoveredEffectMethods,
					discoveredMiddlewares);
			}
		}

		private void FixUpStore(
			IStore store,
			DiscoveredFeatureClass[] discoveredFeatureClasses,
			DiscoveredReducerClass[] discoveredReducerClasses,
			DiscoveredReducerMethod[] discoveredReducerMethods,
			DiscoveredEffectClass[] discoveredEffectClasses,
			DiscoveredEffectMethod[] discoveredEffectMethods,
			DiscoveredMiddleware[] discoveredMiddlewares)
		{
			foreach (DiscoveredEffectClass discoveredEffectClass in discoveredEffectClasses)
			{
				var effect = (IEffect)ObjectBuilder.Build(discoveredEffectClass.ImplementingType);
				store.AddEffect(effect);
			}

			foreach (DiscoveredEffectMethod discoveredEffectMethod in discoveredEffectMethods)
			{
				IEffect effect = EffectWrapperFactory.Create(ObjectBuilder, discoveredEffectMethod);
				store.AddEffect(effect);
			}

			var newFeatures = new List<IFeature>();
			foreach (DiscoveredFeatureClass discoveredFeatureClass in discoveredFeatureClasses)
			{
				var feature = (IFeature)ObjectBuilder.Build(discoveredFeatureClass.ImplementingType);
				newFeatures.Add(feature);
			}
			AddReducers(newFeatures, discoveredReducerClasses, discoveredReducerMethods);
			// Only add features once they have their reducers in place.
			// There is no particular reason for this except it seems right
			// to ensure the feature is in a complete state before adding
			// it to the store.
			foreach (IFeature newFeature in newFeatures)
				store.AddFeature(newFeature);

			// Note: We have no way of knowing what a Middleware needs access to
			// (Store, features, etc) - so add Middlewares last to ensure everything
			// is in place before they are initialised
			AddMiddlewares(store, discoveredMiddlewares);
		}

		private void AddMiddlewares(IStore store, DiscoveredMiddleware[] discoveredMiddlewares)
		{
			Type[] autoLoadedMiddlewareTypes = discoveredMiddlewares
				.Where(x => x.AutoLoaded)
				.Select(x => x.ImplementingType)
				.Distinct()
				.ToArray();

			Type[] middlewareTypes = discoveredMiddlewares
				.Select(x => x.ImplementingType)
				.Union(autoLoadedMiddlewareTypes)
				.Distinct()
				.ToArray();

			foreach (Type middlewareType in middlewareTypes)
			{
				var middleware = (IMiddleware)ObjectBuilder.Build(middlewareType);
				store.AddMiddleware(middleware);
			}
		}

		private void AddReducers(IEnumerable<IFeature> newFeatures, DiscoveredReducerClass[] discoveredReducerClasses, DiscoveredReducerMethod[] discoveredReducerMethods)
		{
			Dictionary<Type, IGrouping<Type, DiscoveredReducerClass>> discoveredReducerClassesByStateType =
				discoveredReducerClasses
				.GroupBy(x => x.StateType)
				.ToDictionary(x => x.Key);
			Dictionary<Type, IGrouping<Type, DiscoveredReducerMethod>> discoveredReducerMethodsByStateType =
				discoveredReducerMethods
					.GroupBy(x => x.StateType)
					.ToDictionary(x => x.Key);

			string addFeatureMethodName = nameof(IFeature<object>.AddReducer);
			foreach (IFeature featureInstance in newFeatures)
			{
				Type stateType = featureInstance.GetStateType();
				MethodInfo addReducerMethod = featureInstance.GetType().GetMethod(addFeatureMethodName);
				if (discoveredReducerClassesByStateType.TryGetValue(stateType, out var classReducers))
				{
					foreach (var classReducer in classReducers)
					{
						object reducerInstance = ObjectBuilder.Build(classReducer.ImplementingType);
						addReducerMethod.Invoke(featureInstance, new object[] { reducerInstance });
					}
				}

				if (discoveredReducerMethodsByStateType.TryGetValue(stateType, out var methodReducers))
				{
					foreach (var methodReducer in methodReducers)
					{
						object reducerWrapperInstance = ReducerWrapperFactory.Create(ObjectBuilder, methodReducer);
						addReducerMethod.Invoke(featureInstance, new object[] { reducerWrapperInstance });
					}
				}
			}

		}
	}
}
