// Copyright (c) IEvangelist. All rights reserved.
// Licensed under the MIT License.

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Azure.CosmosEventSourcing.Aggregates;
using Microsoft.Azure.CosmosEventSourcing.Attributes;
using Microsoft.Azure.CosmosEventSourcing.Events;
using Microsoft.Azure.CosmosEventSourcing.Exceptions;
using Microsoft.Azure.CosmosEventSourcing.Extensions;
using Microsoft.Azure.CosmosEventSourcing.Items;
using Microsoft.Azure.CosmosRepository;
using Microsoft.Azure.CosmosRepository.Paging;

namespace Microsoft.Azure.CosmosEventSourcing.Stores;

internal class DefaultEventStore<TEventItem> :
    IEventStore<TEventItem> where TEventItem : EventItem
{
    private readonly IRepository<TEventItem> _repository;

    public DefaultEventStore(IRepository<TEventItem> repository) =>
        _repository = repository;

    public async ValueTask PersistAsync(
        IEnumerable<TEventItem> items,
        CancellationToken cancellationToken = default)
    {
        List<TEventItem> eventItems = items.ToList();
        if (eventItems is { Count: 0 })
        {
            return;
        }

        if (eventItems.Count(x => x.EventName is nameof(AtomicEvent)) is not 1)
        {
            throw new AtomicEventRequiredException();
        }

        await _repository.UpdateAsBatchAsync(
            eventItems,
            cancellationToken);
    }

    public async ValueTask PersistAsync(
        IAggregateRoot aggregateRoot,
        CancellationToken cancellationToken = default) =>
            await PersistAsync(
                aggregateRoot,
                GetEventItemPartitionKeyValue(aggregateRoot),
                cancellationToken);

    public async ValueTask PersistAsync(
        IAggregateRoot aggregateRoot,
        string partitionKeyValue,
        CancellationToken cancellationToken = default) =>
            await PersistAsync(
                BuildEvents(aggregateRoot, partitionKeyValue),
                cancellationToken);

    public ValueTask<IEnumerable<TEventItem>> ReadAsync(string partitionKey,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(
            x => x.PartitionKey == partitionKey,
            cancellationToken);

    public ValueTask<IEnumerable<TEventItem>> ReadAsync(
        string partitionKey,
        Expression<Func<TEventItem, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(
            predicate.Compose(
                x => x.PartitionKey == partitionKey,
                Expression.AndAlso),
            cancellationToken);

    public async IAsyncEnumerable<TEventItem> StreamAsync(
        string partitionKey,
        int chunkSize = 25,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? token = null;

        Expression<Func<TEventItem, bool>> expression = eventSource =>
            eventSource.PartitionKey == partitionKey;

        do
        {
            IPage<TEventItem> page = await _repository.PageAsync(
                expression,
                chunkSize,
                token,
                cancellationToken: cancellationToken);

            token = page.Continuation;

            foreach (TEventItem eventSource in page.Items)
            {
                yield return eventSource;
            }
        } while (token is not null);
    }

    private static List<TEventItem> BuildEvents(IAggregateRoot aggregateRoot, string partitionKey)
    {
        List<TEventItem?> events = aggregateRoot.NewEvents
            .Select(x =>
                Activator.CreateInstance(
                    typeof(TEventItem),
                    x,
                    partitionKey) as TEventItem)
            .ToList();

        events.Add(Activator.CreateInstance(
            typeof(TEventItem),
            aggregateRoot.AtomicEvent,
            partitionKey) as TEventItem);

        if (events.Any(x => x == null))
        {
            throw new InvalidOperationException(
                $"At least one of the {typeof(TEventItem).Name} could not be constructed");
        }

        return events!;
    }

    private static string GetEventItemPartitionKeyValue<TAggregate>(TAggregate aggregate)
        where TAggregate : IAggregateRoot
    {
        List<PropertyInfo> partitionKeyProperties = aggregate
            .GetType()
            .GetProperties()
            .Where(x
                => x.GetCustomAttributes()
                    .Any(y =>
                        y is EventItemPartitionKeyAttribute))
            .ToList();

        switch (partitionKeyProperties.Count)
        {
            case 0:
                throw new EventItemPartitionKeyAttributeRequiredException(aggregate.GetType());
            case > 1:
                throw new InvalidEventItemPartitionKeyAttributeCombinationException(aggregate.GetType());
        }

        PropertyInfo partitionKeyProperty = partitionKeyProperties.Single();
        Object partitionKey = partitionKeyProperty.GetValue(aggregate) ??
                              throw new InvalidPartitionKeyValueException(
                                  partitionKeyProperty.Name,
                                  aggregate.GetType());

        return partitionKey.ToString();
    }
}