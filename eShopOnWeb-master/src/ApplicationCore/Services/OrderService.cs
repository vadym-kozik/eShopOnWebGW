using Ardalis.GuardClauses;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Azure.Messaging.ServiceBus;
using System.Net.Http;
using System.Net.Mime;

namespace Microsoft.eShopWeb.ApplicationCore.Services
{
    public class OrderService : IOrderService
    {
        private readonly IAsyncRepository<Order> _orderRepository;
        private readonly IUriComposer _uriComposer;
        private readonly IAsyncRepository<Basket> _basketRepository;
        private readonly IAsyncRepository<CatalogItem> _itemRepository;
        private readonly IConfiguration _configuration;

        public OrderService(IAsyncRepository<Basket> basketRepository,
            IAsyncRepository<CatalogItem> itemRepository,
            IAsyncRepository<Order> orderRepository,
            IUriComposer uriComposer,
            IConfiguration configuration)
        {
            _orderRepository = orderRepository;
            _uriComposer = uriComposer;
            _basketRepository = basketRepository;
            _itemRepository = itemRepository;
            _configuration = configuration;
        }

        public async Task CreateOrderAsync(int basketId, Address shippingAddress)
        {
            var basketSpec = new BasketWithItemsSpecification(basketId);
            var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

            Guard.Against.NullBasket(basketId, basket);
            Guard.Against.EmptyBasketOnCheckout(basket.Items);

            var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
            var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

            var items = basket.Items.Select(basketItem =>
            {
                var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
                var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
                var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
                return orderItem;
            }).ToList();

            var order = new Order(basket.BuyerId, shippingAddress, items);

            await AddOrderToSqlDBAsync(order);
            await AddOrderToQueueAsync(order);
            await AddOrderToCosmosDbAsync(order);

        }

        private async Task AddOrderToSqlDBAsync(Order order)
        {
            await _orderRepository.AddAsync(order);
        }

        private async Task AddOrderToQueueAsync(Order order)
        {
            var client = new ServiceBusClient(_configuration["SB_NAMESPACE_CONNECTION_STRING"]);
            var sender = client.CreateSender(_configuration["SB_QUEUE_NAME"]);

            try
            {
                await sender.SendMessageAsync(new ServiceBusMessage(Newtonsoft.Json.JsonConvert.SerializeObject(order)));
            }
            finally
            {
                await sender.DisposeAsync();
                await client.DisposeAsync();
            }
        }

        private async Task AddOrderToCosmosDbAsync(Order order)
        {
            var httpClient = new HttpClient();

            try
            {
                await httpClient.PostAsync(
                    _configuration["DELIVERY_ORDER_PROCESSOR_FUNCTION"],
                    new StringContent(
                        Newtonsoft.Json.JsonConvert.SerializeObject(order),
                        System.Text.Encoding.UTF8, MediaTypeNames.Application.Json));
            }
            finally
            {
                httpClient.Dispose();
            }

            await _orderRepository.AddAsync(order);
        }
    }
}
