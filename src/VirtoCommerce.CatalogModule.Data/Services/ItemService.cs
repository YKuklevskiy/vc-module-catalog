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
using VirtoCommerce.Platform.Core.Assets;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Data.Infrastructure;
using VirtoCommerce.Platform.Data.GenericCrud;
using VirtoCommerce.Platform.Core.GenericCrud;

namespace VirtoCommerce.CatalogModule.Data.Services
{
    public class ItemService : CrudService<CatalogProduct, ItemEntity, ProductChangingEvent, ProductChangedEvent>, IItemService
    {
        private readonly AbstractValidator<IHasProperties> _hasPropertyValidator;
        private readonly ICrudService<Catalog> _catalogServiceCrud;
        private readonly ICategoryService _categoryService;
        private readonly IOutlineService _outlineService;
        private readonly IBlobUrlResolver _blobUrlResolver;
        private readonly ISkuGenerator _skuGenerator;
        private readonly AbstractValidator<CatalogProduct> _productValidator;

        public ItemService(Func<ICatalogRepository> catalogRepositoryFactory,
            IEventPublisher eventPublisher,
            AbstractValidator<IHasProperties> hasPropertyValidator,
            ICatalogService catalogService,
            ICategoryService categoryService,
            IOutlineService outlineService,
            IPlatformMemoryCache platformMemoryCache,
            IBlobUrlResolver blobUrlResolver,
            ISkuGenerator skuGenerator,
            AbstractValidator<CatalogProduct> productValidator)
             : base(catalogRepositoryFactory, platformMemoryCache, eventPublisher)
        {
            _hasPropertyValidator = hasPropertyValidator;
            _categoryService = categoryService;
            _outlineService = outlineService;
            _blobUrlResolver = blobUrlResolver;
            _skuGenerator = skuGenerator;
            _productValidator = productValidator;
            _catalogServiceCrud = (ICrudService<Catalog>)catalogService;
        }

        #region IItemService Members

        public virtual async Task<CatalogProduct[]> GetByIdsAsync(string[] itemIds, string respGroup, string catalogId = null)
        {
            var itemResponseGroup = EnumUtility.SafeParseFlags(respGroup, ItemResponseGroup.ItemLarge);

            var cacheKey = CacheKey.With(GetType(), nameof(GetByIdsAsync), string.Join("-", itemIds), itemResponseGroup.ToString(), catalogId);
            var result = await _platformMemoryCache.GetOrCreateExclusiveAsync(cacheKey, async (cacheEntry) =>
            {
                var products = Array.Empty<CatalogProduct>();

                if (!itemIds.IsNullOrEmpty())
                {
                    using (var repository = _repositoryFactory())
                    {
                        //Optimize performance and CPU usage
                        repository.DisableChangesTracking();

                        //It is so important to generate change tokens for all ids even for not existing objects to prevent an issue
                        //with caching of empty results for non - existing objects that have the infinitive lifetime in the cache
                        //and future unavailability to create objects with these ids.
                        cacheEntry.AddExpirationToken(ItemCacheRegion.CreateChangeToken(itemIds));
                        cacheEntry.AddExpirationToken(CatalogCacheRegion.CreateChangeToken());

                        products = (await ((ICatalogRepository)repository).GetItemByIdsAsync(itemIds, respGroup))
                            .Select(x => x.ToModel(AbstractTypeFactory<CatalogProduct>.TryCreateInstance()))
                            .ToArray();
                    }

                    if (products.Any())
                    {
                        products = products.OrderBy(x => Array.IndexOf(itemIds, x.Id)).ToArray();
                        await LoadDependenciesAsync(products);
                        ApplyInheritanceRules(products);

                        var productsWithVariationsList = products.Concat(products.Where(p => p.Variations != null)
                            .SelectMany(p => p.Variations)).ToArray();

                        // Fill outlines for products and variations
                        if (itemResponseGroup.HasFlag(ItemResponseGroup.Outlines))
                        {
                            _outlineService.FillOutlinesForObjects(productsWithVariationsList, catalogId);
                        }
                        //Add change tokens for products with variations
                        cacheEntry.AddExpirationToken(ItemCacheRegion.CreateChangeToken(productsWithVariationsList));

                        //Reduce details according to response group
                        foreach (var product in productsWithVariationsList)
                        {
                            product.ReduceDetails(itemResponseGroup.ToString());
                        }
                    }
                }

                return products;
            });

            return result.Select(x => x.Clone() as CatalogProduct).ToArray();
        }

        public virtual async Task<CatalogProduct> GetByIdAsync(string itemId, string responseGroup, string catalogId = null)
        {
            CatalogProduct result = null;

            if (!string.IsNullOrEmpty(itemId))
            {
                var results = await GetByIdsAsync(new[] { itemId }, responseGroup, catalogId);
                result = results.Any() ? results.First() : null;
            }

            return result;
        }

        public virtual Task SaveChangesAsync(CatalogProduct[] items)
        {
            return base.SaveChangesAsync(items);
        }

        public virtual Task DeleteAsync(string[] itemIds)
        {
            return base.DeleteAsync(itemIds);
        }

        public override async Task DeleteAsync(IEnumerable<string> ids, bool softDelete = false)
        {
            var items = await GetByIdsAsync(ids, ItemResponseGroup.ItemInfo.ToString());
            var changedEntries = items
                .Select(i => new GenericChangedEntry<CatalogProduct>(i, EntryState.Deleted))
                .ToList();

            using (var repository = _repositoryFactory())
            {
                await _eventPublisher.Publish(new ProductChangingEvent(changedEntries));

                await ((ICatalogRepository)repository).RemoveItemsAsync(ids.ToArray());
                await repository.UnitOfWork.CommitAsync();

                ClearCache(items);

                await _eventPublisher.Publish(new ProductChangedEvent(changedEntries));
            }
        }

        #endregion IItemService Members

        protected override void ClearCache(IEnumerable<CatalogProduct> models)
        {
            AssociationSearchCacheRegion.ExpireRegion();
            SeoInfoCacheRegion.ExpireRegion();

            foreach (var entity in models)
            {
                ItemCacheRegion.ExpireEntity(entity);
            }
        }

        public virtual async Task LoadDependenciesAsync(IEnumerable<CatalogProduct> products)
        {
            //TODO: refactor to do this by one call and iteration
            var catalogsIds = new { products }.GetFlatObjectsListWithInterface<IHasCatalogId>().Select(x => x.CatalogId).Where(x => x != null).Distinct().ToArray();
            var catalogsByIdDict = (await _catalogServiceCrud.GetByIdsAsync(catalogsIds)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

            var categoriesIds = new { products }.GetFlatObjectsListWithInterface<IHasCategoryId>().Select(x => x.CategoryId).Where(x => x != null).Distinct().ToArray();
            var categoriesByIdDict = (await _categoryService.GetByIdsAsync(categoriesIds, CategoryResponseGroup.Full.ToString())).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

            var allImages = new { products }.GetFlatObjectsListWithInterface<IHasImages>().Where(x => x.Images != null).SelectMany(x => x.Images);
            foreach (var image in allImages.Where(x => !string.IsNullOrEmpty(x.Url)))
            {
                image.RelativeUrl = !string.IsNullOrEmpty(image.RelativeUrl) ? image.RelativeUrl : image.Url;
                image.Url = _blobUrlResolver.GetAbsoluteUrl(image.Url);
            }

            foreach (var product in products)
            {
                SetProductDependencies(product, catalogsByIdDict, categoriesByIdDict);

                if (product.MainProduct != null)
                {
                    SetProductDependencies(product.MainProduct, catalogsByIdDict, categoriesByIdDict);
                }
                if (!product.Variations.IsNullOrEmpty())
                {
                    foreach (var variation in product.Variations)
                    {
                        SetProductDependencies(variation, catalogsByIdDict, categoriesByIdDict);
                    }
                }
            }
        }

        protected virtual void SetProductDependencies(CatalogProduct product, IDictionary<string, Catalog> catalogsByIdDict, IDictionary<string, Category> categoriesByIdDict)
        {
            //TOD: Refactor after cover by the unit tests
            if (string.IsNullOrEmpty(product.Code))
            {
                product.Code = _skuGenerator.GenerateSku(product);
            }
            product.Catalog = catalogsByIdDict.GetValueOrThrow(product.CatalogId, $"catalog with key {product.CatalogId} doesn't exist");
            if (product.CategoryId != null)
            {
                product.Category = categoriesByIdDict.GetValueOrThrow(product.CategoryId, $"category with key {product.CategoryId} doesn't exist");
            }

            foreach (var link in product.Links ?? Array.Empty<CategoryLink>())
            {
                link.Catalog = catalogsByIdDict.GetValueOrThrow(link.CatalogId, $"link catalog with key {link.CatalogId} doesn't exist");

                if (!string.IsNullOrEmpty(link.CategoryId))
                {
                    link.Category = categoriesByIdDict.GetValueOrThrow(link.CategoryId, $"link category with key {link.CategoryId} doesn't exist").Clone() as Category;
                    var necessaryGroups = CategoryResponseGroup.WithProperties | CategoryResponseGroup.WithParents | CategoryResponseGroup.WithSeo;
                    link.Category.ReduceDetails(necessaryGroups.ToString());
                }
            }
        }

        protected virtual void ApplyInheritanceRules(IEnumerable<CatalogProduct> products)
        {
            foreach (var product in products)
            {
                if (product.MainProduct != null)
                {
                    //need to apply inheritance rules for main product first.
                    product.MainProduct.TryInheritFrom(product.Category ?? (IEntity)product.Catalog);
                    product.TryInheritFrom(product.MainProduct);
                }
                else
                {
                    product.TryInheritFrom(product.Category ?? (IEntity)product.Catalog);
                }
            }
        }

        protected virtual async Task ValidateProductsAsync(CatalogProduct[] products)
        {
            if (products == null)
            {
                throw new ArgumentNullException(nameof(products));
            }

            //Validate products
            foreach (var product in products)
            {
                _productValidator.ValidateAndThrow(product);
            }

            await LoadDependenciesAsync(products);
            ApplyInheritanceRules(products);

            var targets = products.OfType<IHasProperties>();
            foreach (var item in targets)
            {
                var validationResult = await _hasPropertyValidator.ValidateAsync(item);
                if (!validationResult.IsValid)
                {
                    throw new ValidationException($"Product properties has validation error: {string.Join(Environment.NewLine, validationResult.Errors.Select(x => x.ToString()))}");
                }
            }
        }

        protected async override Task<IEnumerable<ItemEntity>> LoadEntities(IRepository repository, IEnumerable<string> ids, string responseGroup)
        {
            return await ((ICatalogRepository)repository).GetItemByIdsAsync(ids.ToArray());
        }

        protected override Task BeforeSaveChanges(IEnumerable<CatalogProduct> models)
        {
            return ValidateProductsAsync(models.ToArray());
        }
    }
}
