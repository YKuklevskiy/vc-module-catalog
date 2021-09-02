using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.Extensions.Caching.Memory;
using VirtoCommerce.CatalogModule.Core.Events;
using VirtoCommerce.CatalogModule.Core.Model;
using VirtoCommerce.CatalogModule.Core.Services;
using VirtoCommerce.CatalogModule.Data.Caching;
using VirtoCommerce.CatalogModule.Data.Model;
using VirtoCommerce.CatalogModule.Data.Repositories;
using VirtoCommerce.CatalogModule.Data.Validation;
using VirtoCommerce.Platform.Core.Assets;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Exceptions;
using VirtoCommerce.Platform.Data.Infrastructure;
using VirtoCommerce.Platform.Data.GenericCrud;

namespace VirtoCommerce.CatalogModule.Data.Services
{
    public class CategoryService : CrudService<Category, CategoryEntity, CategoryChangingEvent, CategoryChangedEvent>, ICategoryService
    {
        private new readonly IPlatformMemoryCache _platformMemoryCache;
        private new readonly Func<ICatalogRepository> _repositoryFactory;
        private new readonly IEventPublisher _eventPublisher;
        private readonly AbstractValidator<IHasProperties> _hasPropertyValidator;
        private readonly ICatalogService _catalogService;
        private readonly IOutlineService _outlineService;
        private readonly IBlobUrlResolver _blobUrlResolver;

        public CategoryService(
            Func<ICatalogRepository> catalogRepositoryFactory
            , IEventPublisher eventPublisher
            , IPlatformMemoryCache platformMemoryCache
            , AbstractValidator<IHasProperties> hasPropertyValidator
            , ICatalogService catalogService
            , IOutlineService outlineService
            , IBlobUrlResolver blobUrlResolver)
            : base(catalogRepositoryFactory, platformMemoryCache, eventPublisher)
        {
            _repositoryFactory = catalogRepositoryFactory;
            _eventPublisher = eventPublisher;
            _platformMemoryCache = platformMemoryCache;
            _hasPropertyValidator = hasPropertyValidator;
            _outlineService = outlineService;
            _blobUrlResolver = blobUrlResolver;
            _catalogService = catalogService;
        }

        #region ICategoryService Members

        public virtual async Task<Category[]> GetByIdsAsync(string[] categoryIds, string responseGroup, string catalogId = null)
        {
            var categoryResponseGroup = EnumUtility.SafeParseFlags(responseGroup, CategoryResponseGroup.Full);

            var result = new List<Category>();
            var preloadedCategoriesByIdDict = await PreloadCategoriesAsync(catalogId);
            foreach (var categoryId in categoryIds.Where(x => x != null))
            {
                var category = preloadedCategoriesByIdDict[categoryId];
                if (category != null)
                {
                    category = category.Clone() as Category;
                    //Reduce details according to response group
                    category.ReduceDetails(categoryResponseGroup.ToString());
                    result.Add(category);
                }
            }
            return result.ToArray();
        }

        public virtual async Task SaveChangesAsync(Category[] categories)
        {
            await base.SaveChangesAsync(categories);
        }

        public virtual async Task DeleteAsync(string[] categoryIds)
        {
            var categories = await GetByIdsAsync(categoryIds, CategoryResponseGroup.Info.ToString());
            var changedEntries = categories
                .Select(c => new GenericChangedEntry<Category>(c, EntryState.Deleted))
                .ToList();

            using (var repository = _repositoryFactory())
            {
                await _eventPublisher.Publish(new CategoryChangingEvent(changedEntries));
                await repository.RemoveCategoriesAsync(categoryIds);
                await repository.UnitOfWork.CommitAsync();

                ClearCache(categories);
                await _eventPublisher.Publish(new CategoryChangedEvent(changedEntries));
            }
        }

        #endregion

        protected virtual async Task<IDictionary<string, Category>> PreloadCategoriesAsync(string catalogId)
        {
            var cacheKey = CacheKey.With(GetType(), "PreloadCategories", catalogId);
            return await _platformMemoryCache.GetOrCreateExclusiveAsync(cacheKey, async (cacheEntry) =>
           {
               cacheEntry.AddExpirationToken(CatalogCacheRegion.CreateChangeToken());

               var entities = new List<CategoryEntity>();
               using (var repository = _repositoryFactory())
               {
                   repository.DisableChangesTracking();
                   var categoriesIds = repository.Categories.Select(x => x.Id).ToArray();
                   foreach (var page in categoriesIds.Paginate(50))
                   {
                       entities.AddRange(await repository.GetCategoriesByIdsAsync(page.ToArray(), CategoryResponseGroup.Full.ToString()));
                   }
               }
               var result = entities.Select(x => x.ToModel(AbstractTypeFactory<Category>.TryCreateInstance()))
                                    .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase)
                                    .WithDefaultValue(null);

               await LoadDependenciesAsync(result.Values, result);
               ApplyInheritanceRules(result.Values);

               // Fill outlines for categories            
               _outlineService.FillOutlinesForObjects(result.Values, catalogId);
               return result;
           });
        }

        protected virtual async Task LoadDependenciesAsync(IEnumerable<Category> categories, IDictionary<string, Category> preloadedCategoriesMap)
        {
            var catalogsIds = new { categories }.GetFlatObjectsListWithInterface<IHasCatalogId>().Select(x => x.CatalogId).Where(x => x != null).Distinct().ToArray();
            var catalogsByIdDict = (await _catalogService.GetByIdsAsync(catalogsIds)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

            //Resolve relative urls for all category assets
            var allImages = new { categories }.GetFlatObjectsListWithInterface<IHasImages>().Where(x => x.Images != null).SelectMany(x => x.Images);
            foreach (var image in allImages.Where(x => !string.IsNullOrEmpty(x.Url)))
            {
                image.RelativeUrl = !string.IsNullOrEmpty(image.RelativeUrl) ? image.RelativeUrl : image.Url;
                image.Url = _blobUrlResolver.GetAbsoluteUrl(image.Url);
            }

            foreach (var category in categories)
            {
                category.Catalog = catalogsByIdDict.GetValueOrThrow(category.CatalogId, $"catalog with key {category.CatalogId} doesn't exist");
                category.IsVirtual = category.Catalog.IsVirtual;
                category.Parents = Array.Empty<Category>();
                //Load all parent categories
                if (category.ParentId != null)
                {
                    category.Parents = TreeExtension.GetAncestors(category, x => x.ParentId != null ? preloadedCategoriesMap[x.ParentId] : null)
                                                    .Reverse()
                                                    .ToArray();
                    category.Parent = category.Parents.LastOrDefault();
                }
                category.Level = category.Parents?.Count() ?? 0;

                foreach (var link in category.Links ?? Array.Empty<CategoryLink>())
                {
                    link.Catalog = catalogsByIdDict.GetValueOrThrow(link.CatalogId, $"link catalog with key {link.CatalogId} doesn't exist");
                    if (link.CategoryId != null)
                    {
                        link.Category = preloadedCategoriesMap[link.CategoryId];
                    }
                }

                foreach (var property in category.Properties ?? Array.Empty<Property>())
                {
                    property.Catalog = property.CatalogId != null ? catalogsByIdDict[property.CatalogId] : null;
                    if (property.CategoryId != null)
                    {
                        property.Category = preloadedCategoriesMap[property.CategoryId];
                    }
                }

            }
        }

        protected virtual void ApplyInheritanceRules(IEnumerable<Category> categories)
        {
            foreach (var category in categories.OrderBy(x => x.Level))
            {
                category.TryInheritFrom(category.Parent ?? (IEntity)category.Catalog);
            }
        }

        protected virtual async Task ValidateCategoryPropertiesAsync(IEnumerable<Category> categories)
        {
            if (categories == null)
            {
                throw new ArgumentNullException(nameof(categories));
            }
            //Validate categories 
            var validator = new CategoryValidator();
            foreach (var category in categories)
            {
                validator.ValidateAndThrow(category);
            }

            var groups = categories.GroupBy(x => x.CatalogId);
            foreach (var group in groups)
            {
                await LoadDependenciesAsync(group, await PreloadCategoriesAsync(group.Key));
                ApplyInheritanceRules(group);

                foreach (var category in group)
                {
                    var validationResult = _hasPropertyValidator.Validate(category);
                    if (!validationResult.IsValid)
                    {
                        throw new PlatformException($"Category properties has validation error: {string.Join(Environment.NewLine, validationResult.Errors.Select(x => x.ToString()))}");
                    }
                }
            }
        }

        protected override void ClearCache(IEnumerable<Category> models)
        {
            CatalogCacheRegion.ExpireRegion();
            SeoInfoCacheRegion.ExpireRegion();
        }

        protected async override Task<IEnumerable<CategoryEntity>> LoadEntities(IRepository repository, IEnumerable<string> ids, string responseGroup)
        {
            return await ((ICatalogRepository)repository).GetCategoriesByIdsAsync(ids.ToArray(), CategoryResponseGroup.Full.ToString());
        }

        protected async override Task BeforeSaveChanges(IEnumerable<Category> models)
        {
            await ValidateCategoryPropertiesAsync(models);
        }
    }
}
