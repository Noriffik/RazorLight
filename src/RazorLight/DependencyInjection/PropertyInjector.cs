using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RazorLight.Internal;

namespace RazorLight.DependencyInjection
{
	public class PropertyInjector
	{
		private readonly IServiceProvider _services;
		private readonly ConcurrentDictionary<PropertyInfo, FastPropertySetter> _propertyCache;

		public PropertyInjector(IServiceProvider services)
		{
			_services = services ?? throw new ArgumentNullException(nameof(services));
			_propertyCache = new ConcurrentDictionary<PropertyInfo, FastPropertySetter>();
		}

		public void Inject(ITemplatePage page)
		{
			if (page == null)
			{
				throw new ArgumentNullException(nameof(page));
			}

			var properties = page.GetType().GetRuntimeProperties()
			   .Where(p => p.IsDefined(typeof(RazorInjectAttribute)) &&
			               p.GetIndexParameters().Length == 0 &&
			               !p.SetMethod.IsStatic).ToArray();

			var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();

			using (var scope = scopeFactory.CreateScope())
			{
				var scopeServices = scope.ServiceProvider;

				foreach (var property in properties)
				{
					var memberType = property.PropertyType;
					var instance = scopeServices.GetRequiredService(memberType);

					var setter = _propertyCache.GetOrAdd(property, new FastPropertySetter(property));
					setter.SetValue(page, instance);
				}
			}
		}
	}
}
