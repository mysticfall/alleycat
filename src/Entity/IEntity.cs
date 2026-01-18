namespace AlleyCat.Entity;

public interface IEntityId
{
    string Value { get; }
}

public interface IEntity
{
    IEntityId Id { get; }
}

public interface IEntity<out TId> : IEntity where TId : IEntityId
{
    new TId Id { get; }
}