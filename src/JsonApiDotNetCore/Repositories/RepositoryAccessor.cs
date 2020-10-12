using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JsonApiDotNetCore.Configuration;
using JsonApiDotNetCore.Queries;
using JsonApiDotNetCore.Queries.Expressions;
using JsonApiDotNetCore.Resources;
using JsonApiDotNetCore.Resources.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace JsonApiDotNetCore.Repositories
{
    /// <inheritdoc />
    public class RepositoryAccessor : IRepositoryAccessor
    {
        private static readonly Type _openResourceReadRepositoryType = typeof(IResourceReadRepository<,>);
        private static readonly MethodInfo _openGetByIdMethod;

        static RepositoryAccessor()
        {
            _openGetByIdMethod = typeof(RepositoryAccessor).GetMethod(nameof(GetById), BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private readonly IServiceProvider _serviceProvider;
        private readonly IResourceContextProvider _provider;
        private readonly IResourceDefinitionAccessor _resourceDefinitionAccessor;

        private readonly Dictionary<Type, (MethodInfo, object)> _parameterizedMethodRepositoryCache = new Dictionary<Type, (MethodInfo, object)>();

        public RepositoryAccessor(
            IServiceProvider serviceProvider,
            IResourceContextProvider provider,
            IResourceDefinitionAccessor resourceDefinitionAccessor)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentException(nameof(serviceProvider));
            _provider = provider ?? throw new ArgumentException(nameof(serviceProvider));
            _resourceDefinitionAccessor = resourceDefinitionAccessor ?? throw new ArgumentException(nameof(resourceDefinitionAccessor));
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IIdentifiable>> GetResourcesByIdAsync(Type resourceType,
            IReadOnlyCollection<string> ids)
        {
            var resourceContext = _provider.GetResourceContext(resourceType);
            var (getByIdMethod, repository) = GetParameterizedMethodAndRepository(resourceType, resourceContext);
            
            var resources = await InvokeAsync(getByIdMethod, this, new [] { ids, repository, resourceContext });

            return (IEnumerable<IIdentifiable>) resources;
        }

        private (MethodInfo, object) GetParameterizedMethodAndRepository(Type resourceType,
            ResourceContext resourceContext)
        {
            if (!_parameterizedMethodRepositoryCache.TryGetValue(resourceType, out var accessorPair))
            {
                var parameterizedMethod = _openGetByIdMethod.MakeGenericMethod(resourceType, resourceContext.IdentityType);
                
                var repositoryType = _openResourceReadRepositoryType.MakeGenericType(resourceType, resourceContext.IdentityType);
                var repository = _serviceProvider.GetRequiredService(repositoryType);

                accessorPair = (parameterizedMethod, repository);
                _parameterizedMethodRepositoryCache.Add(resourceType, accessorPair);
            }

            return accessorPair;
        }

        private async Task<IEnumerable<IIdentifiable>> GetById<TResource, TId>(
            IEnumerable<string> ids,
            IResourceReadRepository<TResource, TId> repository,
            ResourceContext resourceContext) 
            where TResource : class, IIdentifiable<TId>
        {
            var idAttribute = resourceContext.Attributes.Single(attr => attr.Property.Name == nameof(Identifiable.Id));
            
            var idExpressions = ids.Select(id => new LiteralConstantExpression(id.ToString())).ToArray();
            var equalsAnyOfFilter = new EqualsAnyOfExpression(new ResourceFieldChainExpression(idAttribute), idExpressions);
            
            var queryLayer = new QueryLayer(resourceContext)
            {
                Filter = _resourceDefinitionAccessor.OnApplyFilter(resourceContext.ResourceType, equalsAnyOfFilter)
            };

            // Only apply projection when there is no resource inheritance. See https://github.com/json-api-dotnet/JsonApiDotNetCore/issues/844.
            // We can leave it out because the projection here is just an optimization
            if (!resourceContext.ResourceType.IsAbstract)
            {
                var projection = new Dictionary<ResourceFieldAttribute, QueryLayer> {{idAttribute, null}};
                queryLayer.Projection = projection;
            }

            return await repository.GetAsync(queryLayer);
        }
        
        private async Task<object> InvokeAsync(MethodInfo methodInfo, object target, object[] parameters)
        {
            dynamic task = methodInfo.Invoke(target, parameters);
            await task;
    
            return task.GetAwaiter().GetResult();
        }
    }
}
