﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CuttingEdge.Conditions;
using Netco.Extensions;
using ServiceStack;
using ShopifyAccess.Misc;
using ShopifyAccess.Models;
using ShopifyAccess.Models.Configuration.Command;
using ShopifyAccess.Models.Location;
using ShopifyAccess.Models.Order;
using ShopifyAccess.Models.Product;
using ShopifyAccess.Models.ProductVariant;
using ShopifyAccess.Models.User;
using ShopifyAccess.Services;

namespace ShopifyAccess
{
	public sealed class ShopifyService: IShopifyService
	{
		private readonly WebRequestServices _webRequestServices;
		private const int RequestMaxLimit = 250;
		private const int RequestInventoryLevelsMaxLimit = 50;
		private readonly string _shopName;
		// One throttler for all requests because used same limit for all API
		private readonly ShopifyThrottler _throttler = new ShopifyThrottler();
		private readonly ShopifyThrottlerAsync _throttlerAsync = new ShopifyThrottlerAsync();

		// Separate throttler for updating to save limit for other syncs
		private readonly ShopifyThrottler _productUpdateThrottler = new ShopifyThrottler( 30 );
		private readonly ShopifyThrottlerAsync _productUpdateThrottlerAsync = new ShopifyThrottlerAsync( 30 );

		public ShopifyService( ShopifyCommandConfig config )
		{
			Condition.Requires( config, "config" ).IsNotNull();

			this._webRequestServices = new WebRequestServices( config );
			this._shopName = config.ShopName;

			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
		}

		#region GetOrders
		public ShopifyOrders GetOrders( ShopifyOrderStatus status, DateTime dateFrom, DateTime dateTo, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var updatedOrdersEndpoint = EndpointsBuilder.CreateUpdatedOrdersEndpoint( status, dateFrom, dateTo );
			var orders = this.CollectOrdersFromAllPages( updatedOrdersEndpoint, mark );

			return orders;
		}

		public async Task< ShopifyOrders > GetOrdersAsync( ShopifyOrderStatus status, DateTime dateFrom, DateTime dateTo, CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var updatedOrdersEndpoint = EndpointsBuilder.CreateUpdatedOrdersEndpoint( status, dateFrom, dateTo );
			var orders = await this.CollectOrdersFromAllPagesAsync( updatedOrdersEndpoint, mark, token );

			return orders;
		}

		public ShopifyLocations GetLocations( Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var locations = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
				() => this._throttler.Execute(
					() => this._webRequestServices.GetResponse< ShopifyLocations >( ShopifyCommand.GetLocations, "", mark ) ) );
			return locations;
		}

		public Task< ShopifyLocations > GetLocationsAsync( CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var locations = ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
				() => this._throttlerAsync.ExecuteAsync(
					() => this._webRequestServices.GetResponseAsync< ShopifyLocations >( ShopifyCommand.GetLocations, "", mark, token ) ) );
			return locations;
		}

		private ShopifyOrders CollectOrdersFromAllPages( string mainUpdatedOrdersEndpoint, Mark mark )
		{
			var orders = new ShopifyOrders();
			long sinceId = 0;

			while( true )
			{
				var compositeUpdatedOrdersEndpoint = mainUpdatedOrdersEndpoint.ConcatEndpoints( EndpointsBuilder.CreateGetNextPageSinceIdEndpoint( new ShopifyCommandEndpointConfig( sinceId, RequestMaxLimit ) ) );

				var updatedOrdersWithinPage = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
					() => this._throttler.Execute(
						() => this._webRequestServices.GetResponse< ShopifyOrders >( ShopifyCommand.GetOrders, compositeUpdatedOrdersEndpoint, mark ) ) );

				if( updatedOrdersWithinPage.Orders.Count == 0 )
					break;

				sinceId = updatedOrdersWithinPage.Orders.Max( p => p.Id );
				orders.Orders.AddRange( updatedOrdersWithinPage.Orders );
			}

			return orders;
		}

		private async Task< ShopifyOrders > CollectOrdersFromAllPagesAsync( string mainUpdatedOrdersEndpoint, Mark mark, CancellationToken token )
		{
			var orders = new ShopifyOrders();
			long sinceId = 0;

			while( true )
			{
				var compositeUpdatedOrdersEndpoint = mainUpdatedOrdersEndpoint.ConcatEndpoints( EndpointsBuilder.CreateGetNextPageSinceIdEndpoint( new ShopifyCommandEndpointConfig( sinceId, RequestMaxLimit ) ) );

				var updatedOrdersWithinPage = await ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
					() => this._throttlerAsync.ExecuteAsync(
						() => this._webRequestServices.GetResponseAsync< ShopifyOrders >( ShopifyCommand.GetOrders, compositeUpdatedOrdersEndpoint, mark, token ) ) );

				if( updatedOrdersWithinPage.Orders.Count == 0 )
					break;

				sinceId = updatedOrdersWithinPage.Orders.Max( p => p.Id );
				orders.Orders.AddRange( updatedOrdersWithinPage.Orders );
			}

			return orders;
		}

		private int GetOrdersCount( string updatedOrdersEndpoint, Mark mark )
		{
			var count = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
				() => this._throttler.Execute(
					() => this._webRequestServices.GetResponse< OrdersCount >( ShopifyCommand.GetOrdersCount, updatedOrdersEndpoint, mark ).Count ) );
			return count;
		}

		private Task< int > GetOrdersCountAsync( string updatedOrdersEndpoint, Mark mark, CancellationToken token )
		{
			var count = ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
				() => this._throttlerAsync.ExecuteAsync(
					async () => ( await this._webRequestServices.GetResponseAsync< OrdersCount >( ShopifyCommand.GetOrdersCount, updatedOrdersEndpoint, mark, token ) ).Count ) );
			return count;
		}
		#endregion

		#region Products
		public ShopifyProducts GetProducts( Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var products = this.CollectProductsFromAllPages( mark );
			this.RemoveUntrackedProductVariants( products );
			var inventoryLevels = this.CollectInventoryLevelsFromAllPages( mark, products.Products.SelectMany( x => x.Variants.Select( t => t.InventoryItemId ) ).ToArray() );

			foreach( var product in products.Products )
			foreach( var variant in product.Variants )
			{
				var inventoryLevelsModelOfInventoryItemId = new List< ShopifyInventoryLevelModel >();
				if( !inventoryLevels.InventoryLevels.TryGetValue( variant.InventoryItemId, out inventoryLevelsModelOfInventoryItemId ) )
					continue;

				var inventoryLevelsOfInventoryItemId = inventoryLevelsModelOfInventoryItemId.Select( x => x.ToShopifyInventoryLevel( variant.InventoryItemId ) ).ToList();

				var inventoryLevelsForVariant = new ShopifyInventoryLevels { InventoryLevels = inventoryLevelsOfInventoryItemId };
				variant.InventoryLevels = inventoryLevelsForVariant;
			}

			return products;
		}

		public async Task< ShopifyProducts > GetProductsAsync( CancellationToken token, Mark mark = null )
		{
			return await this.GetProductsCreatedAfterAsync( DateTime.MinValue, token, mark );
		}
		
		public async Task< ShopifyProducts > GetProductsCreatedAfterAsync( DateTime productsStartUtc, CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var productsDateFilter = new ProductsDateFilter
			{
				FilterType = productsStartUtc != DateTime.MinValue ? FilterType.CreatedAfter : FilterType.None,
				ProductsStartUtc = productsStartUtc
			};
			var products = await this.CollectProductsFromAllPagesAsync( productsDateFilter, mark, token );
			await this.GetInventoryLevels( token, mark, products );

			return products;
		}
		
		public async Task< ShopifyProducts > GetProductsCreatedBeforeButUpdatedAfterAsync( DateTime productsStartUtc, CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			if( productsStartUtc == DateTime.MinValue )
			{
				return null;
			}

			var productsDateFilter = new ProductsDateFilter
			{
				FilterType = FilterType.CreatedBeforeUpdatedAfter,
				ProductsStartUtc = productsStartUtc
			};
			var products = await this.CollectProductsFromAllPagesAsync( productsDateFilter, mark, token );
			await this.GetInventoryLevels( token, mark, products );

			return products;
		}

		private async Task GetInventoryLevels( CancellationToken token, Mark mark, ShopifyProducts products )
		{
			this.RemoveUntrackedProductVariants( products );
			var inventoryLevels = await this.CollectInventoryLevelsFromAllPagesAsync( mark, products.Products.SelectMany( x => x.Variants.Select( t => t.InventoryItemId ) ).ToArray(), token );

			foreach( var product in products.Products )
			foreach( var variant in product.Variants )
			{
				List< ShopifyInventoryLevelModel > inventoryLevelsModelOfInventoryItemId;
				if( !inventoryLevels.InventoryLevels.TryGetValue( variant.InventoryItemId, out inventoryLevelsModelOfInventoryItemId ) )
					continue;

				var inventoryLevelsOfInventoryItemId = inventoryLevelsModelOfInventoryItemId.Select( x => x.ToShopifyInventoryLevel( variant.InventoryItemId ) ).ToList();

				var inventoryLevelsForVariant = new ShopifyInventoryLevels { InventoryLevels = inventoryLevelsOfInventoryItemId };
				variant.InventoryLevels = inventoryLevelsForVariant;
			}
		}

		public ShopifyProducts GetProductsThroughLocations( Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var products = this.CollectProductsFromAllPages( mark );
			var locations = this.GetLocations( mark );
			this.RemoveUntrackedProductVariants( products );
			var inventoryLevels = this.CollectInventoryLevelsFromAllPages( mark, locations );

			foreach( var product in products.Products )
			foreach( var variant in product.Variants )
			{
				var inventoryLevelsModelOfInventoryItemId = new List< ShopifyInventoryLevelModel >();
				if( !inventoryLevels.InventoryLevels.TryGetValue( variant.InventoryItemId, out inventoryLevelsModelOfInventoryItemId ) )
					continue;

				var inventoryLevelsOfInventoryItemId = inventoryLevelsModelOfInventoryItemId.Select( x => x.ToShopifyInventoryLevel( variant.InventoryItemId ) ).ToList();

				var inventoryLevelsForVariant = new ShopifyInventoryLevels { InventoryLevels = inventoryLevelsOfInventoryItemId };
				variant.InventoryLevels = inventoryLevelsForVariant;
			}

			return products;
		}

		public async Task< ShopifyProducts > GetProductsThroughLocationsAsync( CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var products = await this.CollectProductsFromAllPagesAsync( mark, token );
			var locations = await this.GetLocationsAsync( token, mark );
			this.RemoveUntrackedProductVariants( products );
			var inventoryLevels = await this.CollectInventoryLevelsFromAllPagesAsync( mark, locations, token );

			foreach( var product in products.Products )
			foreach( var variant in product.Variants )
			{
				var inventoryLevelsModelOfInventoryItemId = new List< ShopifyInventoryLevelModel >();
				if( !inventoryLevels.InventoryLevels.TryGetValue( variant.InventoryItemId, out inventoryLevelsModelOfInventoryItemId ) )
					continue;

				var inventoryLevelsOfInventoryItemId = inventoryLevelsModelOfInventoryItemId.Select( x => x.ToShopifyInventoryLevel( variant.InventoryItemId ) ).ToList();

				var inventoryLevelsForVariant = new ShopifyInventoryLevels { InventoryLevels = inventoryLevelsOfInventoryItemId };
				variant.InventoryLevels = inventoryLevelsForVariant;
			}

			return products;
		}

		public async Task< List< ShopifyProductVariant > > GetProductVariantsBySkusAsync( IEnumerable< string > skus, CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();

			var products = await this.CollectProductsFromAllPagesAsync( mark, token );
			this.RemoveUntrackedProductVariants( products );

			var productVariants = products.ToListVariants();
			var variantIds = productVariants.Select( v => new { Sku = v.Sku.ToLowerInvariant(), v.InventoryItemId } );
			var inventoryItemIds = skus.Select( s => s.ToLowerInvariant() ).Distinct()
				.Join( variantIds, s => s, v => v.Sku, ( s, v ) => v.InventoryItemId )
				.ToArray();
			var inventoryLevels = await this.CollectInventoryLevelsFromAllPagesAsync( mark, inventoryItemIds, token );
			productVariants = productVariants
				.Join( inventoryItemIds, v => v.InventoryItemId, iid => iid, ( v, iid ) => v )
				.ToList();

			foreach( var variant in productVariants )
			{
				List< ShopifyInventoryLevelModel > inventoryLevelsModelOfInventoryItemId;
				if( !inventoryLevels.InventoryLevels.TryGetValue( variant.InventoryItemId, out inventoryLevelsModelOfInventoryItemId ) )
					continue;

				var inventoryLevelsOfInventoryItemId = inventoryLevelsModelOfInventoryItemId.Select( x => x.ToShopifyInventoryLevel( variant.InventoryItemId ) ).ToList();

				var inventoryLevelsForVariant = new ShopifyInventoryLevels { InventoryLevels = inventoryLevelsOfInventoryItemId };
				variant.InventoryLevels = inventoryLevelsForVariant;
			}

			return productVariants;
		}

		private int GetProductsCount( Mark mark )
		{
			var count = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
				() => this._throttler.Execute(
					() => this._webRequestServices.GetResponse< ProductsCount >( ShopifyCommand.GetProductsCount, EndpointsBuilder.EmptyEndpoint, mark ).Count ) );
			return count;
		}

		private Task< int > GetProductsCountAsync( Mark mark, CancellationToken token )
		{
			var count = ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
				() => this._throttlerAsync.ExecuteAsync(
					async () => ( await this._webRequestServices.GetResponseAsync< ProductsCount >( ShopifyCommand.GetProductsCount, EndpointsBuilder.EmptyEndpoint, mark, token ) ).Count ) );
			return count;
		}

		private ShopifyProducts CollectProductsFromAllPages( Mark mark )
		{
			var products = new ShopifyProducts();
			long sinceId = 0;

			while( true )
			{
				var endpoint = EndpointsBuilder.CreateGetNextPageSinceIdEndpoint( new ShopifyCommandEndpointConfig( sinceId, RequestMaxLimit ) );

				var productsWithinPage = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
					() => this._throttler.Execute(
						() => this._webRequestServices.GetResponse< ShopifyProducts >( ShopifyCommand.GetProducts, endpoint, mark ) ) );

				if( productsWithinPage.Products.Count == 0 )
					break;

				sinceId = productsWithinPage.Products.Max( p => p.Id );
				products.Products.AddRange( productsWithinPage.Products );
			}

			return products;
		}

		private async Task< ShopifyProducts > CollectProductsFromAllPagesAsync( Mark mark, CancellationToken token )
		{
			var noFilter = new ProductsDateFilter { FilterType = FilterType.None };

			return await this.CollectProductsFromAllPagesAsync( noFilter, mark, token );
		}

		private async Task< ShopifyProducts > CollectProductsFromAllPagesAsync( ProductsDateFilter productsDateFilter, Mark mark, CancellationToken token )
		{
			var products = new ShopifyProducts();
			long sinceId = 0;

			while( true )
			{
				var endpoint = EndpointsBuilder.CreateGetNextPageSinceIdEndpoint( new ShopifyCommandEndpointConfig( sinceId, RequestMaxLimit ) );
				if( productsDateFilter.FilterType != FilterType.None )
				{
					endpoint += EndpointsBuilder.AppendGetProductsFilteredByDateEndpoint( productsDateFilter, endpoint );
				}

				var productsWithinPage = await ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
					() => this._throttlerAsync.ExecuteAsync(
						() => this._webRequestServices.GetResponseAsync< ShopifyProducts >( ShopifyCommand.GetProducts, endpoint, mark, token ) ) );
				
				if( productsWithinPage.Products.Count == 0 )
					break;
				
				sinceId = productsWithinPage.Products.Max( p => p.Id );
				products.Products.AddRange( productsWithinPage.Products );
			}

			return products;
		}

		private void ConvertToShopifyInventoryLevelsModel( ShopifyInventoryLevelsModel inventoryLevels, ShopifyInventoryLevels productsWithinPage )
		{
			var productsWithinPageGroupByInventoryItemId = productsWithinPage.InventoryLevels
				.GroupBy( x => x.InventoryItemId )
				.Select( x => new
				{
					InventoryItemId = x.Key,
					Data = x.Select( t => new ShopifyInventoryLevelModel( t ) ).ToList()
				} )
				.ToList();

			foreach( var item in productsWithinPageGroupByInventoryItemId )
				if( !inventoryLevels.InventoryLevels.ContainsKey( item.InventoryItemId ) )
					inventoryLevels.InventoryLevels.Add( item.InventoryItemId, item.Data );
				else
					inventoryLevels.InventoryLevels[ item.InventoryItemId ].AddRange( item.Data );
		}

		private ShopifyInventoryLevelsModel CollectInventoryLevelsFromAllPages( Mark mark, long[] productIds )
		{
			var inventoryLevels = new ShopifyInventoryLevelsModel();
			var partsOfProductIds = productIds.Slice( RequestInventoryLevelsMaxLimit );

			foreach( var ids in partsOfProductIds )
			{
				var page = 1;
				while( true )
				{
					var endpoint = EndpointsBuilder.CreateInventoryLevelsIdsEndpoint( ids, page, RequestMaxLimit );

					var productsWithinPage = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
						() => this._throttler.Execute(
							() => this._webRequestServices.GetResponse< ShopifyInventoryLevels >( ShopifyCommand.GetInventoryLevels, endpoint, mark ) ) );

					this.ConvertToShopifyInventoryLevelsModel( inventoryLevels, productsWithinPage );

					if( productsWithinPage.InventoryLevels.Count < RequestMaxLimit )
						break;

					page++;
				}
			}

			return inventoryLevels;
		}

		private async Task< ShopifyInventoryLevelsModel > CollectInventoryLevelsFromAllPagesAsync( Mark mark, long[] productIds, CancellationToken token )
		{
			var inventoryLevels = new ShopifyInventoryLevelsModel();
			var partsOfProductIds = productIds.Slice( RequestInventoryLevelsMaxLimit );

			foreach( var ids in partsOfProductIds )
			{
				var page = 1;
				while( true )
				{
					var endpoint = EndpointsBuilder.CreateInventoryLevelsIdsEndpoint( ids, page, RequestMaxLimit );

					var productsWithinPage = await ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
						() => this._throttlerAsync.ExecuteAsync(
							() => this._webRequestServices.GetResponseAsync< ShopifyInventoryLevels >( ShopifyCommand.GetInventoryLevels, endpoint, mark, token ) ) );
					
					this.ConvertToShopifyInventoryLevelsModel( inventoryLevels, productsWithinPage );

					if( productsWithinPage.InventoryLevels.Count < RequestMaxLimit )
						break;

					page++;
				}
			}

			return inventoryLevels;
		}

		private ShopifyInventoryLevelsModel CollectInventoryLevelsFromAllPages( Mark mark, ShopifyLocations shopifyLocations )
		{
			var inventoryLevels = new ShopifyInventoryLevelsModel();
			var partsOfLocationIds = shopifyLocations.Locations.Select( x => x.Id ).Slice( RequestInventoryLevelsMaxLimit );

			foreach( var ids in partsOfLocationIds )
			{
				var page = 1;
				while( true )
				{
					var endpoint = EndpointsBuilder.CreateInventoryLevelsIdsEndpoint( ids, page, RequestMaxLimit );

					var productsWithinPage = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
						() => this._throttler.Execute(
							() => this._webRequestServices.GetResponse< ShopifyInventoryLevels >( ShopifyCommand.GetInventoryLevels, endpoint, mark ) ) );

					this.ConvertToShopifyInventoryLevelsModel( inventoryLevels, productsWithinPage );

					if( productsWithinPage.InventoryLevels.Count < RequestMaxLimit )
						break;

					page++;
				}
			}

			return inventoryLevels;
		}

		private async Task< ShopifyInventoryLevelsModel > CollectInventoryLevelsFromAllPagesAsync( Mark mark, ShopifyLocations shopifyLocations, CancellationToken token )
		{
			var inventoryLevels = new ShopifyInventoryLevelsModel();
			var partsOfLocationIds = shopifyLocations.Locations.Select( x => x.Id ).Slice( RequestInventoryLevelsMaxLimit );

			foreach( var ids in partsOfLocationIds )
			{
				var page = 1;
				while( true )
				{
					var endpoint = EndpointsBuilder.CreateInventoryLevelsIdsEndpoint( ids, page, RequestMaxLimit );

					var productsWithinPage = await ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
						() => this._throttlerAsync.ExecuteAsync(
							() => this._webRequestServices.GetResponseAsync< ShopifyInventoryLevels >( ShopifyCommand.GetInventoryLevels, endpoint, mark, token ) ) );

					this.ConvertToShopifyInventoryLevelsModel( inventoryLevels, productsWithinPage );

					if( productsWithinPage.InventoryLevels.Count < RequestMaxLimit )
						break;

					page++;
				}
			}

			return inventoryLevels;
		}

		private void RemoveUntrackedProductVariants( ShopifyProducts products )
		{
			foreach( var product in products.Products )
			{
				product.Variants.RemoveAll( v => v.InventoryManagement == InventoryManagement.Blank );
			}
		}
		#endregion

		#region Update variants
		public void UpdateProductVariants( IEnumerable< ShopifyProductVariantForUpdate > variants, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			foreach( var variant in variants )
			{
				this.UpdateProductVariantQuantity( variant, mark );
			}
		}
		
		public async Task UpdateProductVariantsAsync( IEnumerable< ShopifyProductVariantForUpdate > variants, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			foreach( var variant in variants )
			{
				await this.UpdateProductVariantQuantityAsync( variant, mark );
			}
		}

		private void UpdateProductVariantQuantity( ShopifyProductVariantForUpdate variant, Mark mark )
		{
			var endpoint = EndpointsBuilder.CreateProductVariantUpdateEndpoint( variant.Id );
			//just simpliest way to serialize with the root name.
			var jsonContent = new { variant }.ToJson();

			ActionPolicies.SubmitPolicy( mark, this._shopName ).Do( () =>
				this._productUpdateThrottler.Execute( () =>
				{
					this._webRequestServices.PutData( ShopifyCommand.UpdateProductVariant, endpoint, jsonContent, mark );
					return true;
				} ) );
		}

		private async Task UpdateProductVariantQuantityAsync( ShopifyProductVariantForUpdate variant, Mark mark )
		{
			var endpoint = EndpointsBuilder.CreateProductVariantUpdateEndpoint( variant.Id );
			var jsonContent = new { variant }.ToJson();

			await ActionPolicies.SubmitPolicyAsync( mark, this._shopName ).Do( async () =>
				await this._productUpdateThrottlerAsync.ExecuteAsync( async () =>
				{
					await this._webRequestServices.PutDataAsync( ShopifyCommand.UpdateProductVariant, endpoint, jsonContent, mark );
					return true;
				} ) );
		}
		
		public void UpdateInventoryLevels( IEnumerable< ShopifyInventoryLevelForUpdate > inventoryLevels, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			foreach( var inventoryLevel in inventoryLevels )
				this.UpdateInventoryLevelQuantity( inventoryLevel, mark );
		}
		
		public async Task UpdateInventoryLevelsAsync( IEnumerable< ShopifyInventoryLevelForUpdate > inventoryLevels, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			foreach( var inventoryLevel in inventoryLevels )
				await this.UpdateInventoryLevelQuantityAsync( inventoryLevel, mark );
		}

		private void UpdateInventoryLevelQuantity( ShopifyInventoryLevelForUpdate variant, Mark mark )
		{
			//just simpliest way to serialize with the root name.
			var jsonContent = variant.ToJson();

			ActionPolicies.SubmitPolicy( mark, this._shopName ).Do( () =>
				this._productUpdateThrottler.Execute( () =>
				{
					this._webRequestServices.PostData< ShopifyInventoryLevelForUpdateResponse >( ShopifyCommand.UpdateInventoryLevels, jsonContent, mark );
					return true;
				} ) );
		}

		private async Task UpdateInventoryLevelQuantityAsync( ShopifyInventoryLevelForUpdate variant, Mark mark )
		{
			//just simpliest way to serialize with the root name.
			var jsonContent = variant.ToJson();

			await ActionPolicies.SubmitPolicyAsync( mark, this._shopName ).Do( async () =>
				await this._productUpdateThrottlerAsync.ExecuteAsync( async () =>
				{
					await this._webRequestServices.PostDataAsync< ShopifyInventoryLevelForUpdateResponse >( ShopifyCommand.UpdateInventoryLevels, jsonContent, mark );
					return true;
				} ) );
		}
		#endregion

		#region Users
		public ShopifyUsers GetUsers( Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var users = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
				() => this._throttler.Execute(
					() => this._webRequestServices.GetResponse< ShopifyUsers >( ShopifyCommand.GetUsers, "", mark ) ) );
			return users;
		}

		public Task< ShopifyUsers > GetUsersAsync( CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var users = ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
				() => this._throttlerAsync.ExecuteAsync(
					() => this._webRequestServices.GetResponseAsync< ShopifyUsers >( ShopifyCommand.GetUsers, "", mark, token ) ) );
			return users;
		}

		public ShopifyUser GetUser( long id, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var user = ActionPolicies.GetPolicy( mark, this._shopName ).Get(
				() => this._throttler.Execute(
					() => this._webRequestServices.GetResponse< ShopifyUserWrapper >( ShopifyCommand.GetUser, EndpointsBuilder.CreateGetUserEndpoint( id ), mark ) ) );
			return user.User;
		}

		public async Task< ShopifyUser > GetUserAsync( long id, CancellationToken token, Mark mark = null )
		{
			mark = mark.CreateNewIfBlank();
			var user = await ActionPolicies.GetPolicyAsync( mark, this._shopName ).Get(
				() => this._throttlerAsync.ExecuteAsync(
					() => this._webRequestServices.GetResponseAsync< ShopifyUserWrapper >( ShopifyCommand.GetUser, EndpointsBuilder.CreateGetUserEndpoint( id ), mark, token ) ) );
			return user.User;
		}

		public bool DoesShopifyPlusAccount( Mark mark = null )
		{
			try
			{
				mark = mark.CreateNewIfBlank();
				this._throttler.Execute(
					() => this._webRequestServices.GetResponse< ShopifyUsers >( ShopifyCommand.GetUsers, "", mark ) );
				return true;
			}
			catch( Exception )
			{
				return false;
			}
		}

		public async Task< bool > DoesShopifyPlusAccountAsync( CancellationToken token, Mark mark = null )
		{
			try
			{
				mark = mark.CreateNewIfBlank();
				await this._throttlerAsync.ExecuteAsync(
					() => this._webRequestServices.GetResponseAsync< ShopifyUsers >( ShopifyCommand.GetUsers, string.Empty, mark, token ) );
				return true;
			}
			catch( Exception )
			{
				return false;
			}
		}
		#endregion

		#region Misc
		private int CalculatePagesCount( int productsCount )
		{
			var result = ( int )Math.Ceiling( ( double )productsCount / RequestMaxLimit );
			return result;
		}
		#endregion
	}
}