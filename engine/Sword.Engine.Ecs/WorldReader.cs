namespace Sword.Engine.Ecs;

/// <summary>Вид только для чтения: интерфейс, члены которого возвращают значения, строки и <c>ReadOnlySpan</c> чистых
/// данных — без сеттеров, массивов, <c>Span</c>, <c>ref</c> и ссылок (<c>PublicSurfaceTests.ReadViews_OnlyRead</c>).</summary>
public interface IReadView;

/// <summary>Мир только для чтения — для наблюдателей симуляции (клиент, инструменты): ни одного пути изменить мир;
/// меняют симуляцию только команды тика у её владельца.</summary>
public readonly partial struct WorldReader
{
    private readonly World _world;

    internal WorldReader(World world) => _world = world;

    /// <summary>Живых сущностей.</summary>
    public int EntityCount => _world.EntityCount;

    /// <summary>Стадии тика по порядку.</summary>
    public IReadOnlyList<string> Stages => _world.Stages;

    /// <summary>Регистрации систем в порядке исполнения (описание мира для людей и инструментов).</summary>
    public IReadOnlyList<SystemInfo> Systems => _world.Systems;

    /// <summary>Зарегистрирован ли компонент (без модуля его нет). Чтение незарегистрированного — исключение.</summary>
    public bool Knows<T>() where T : unmanaged => _world.Knows<T>();

    public bool IsAlive(Entity e) => _world.IsAlive(e);

    public bool Has<T>(Entity e) where T : unmanaged => _world.Has<T>(e);

    /// <inheritdoc cref="World.Get{T}(Entity)"/>
    public T Get<T>(Entity e) where T : unmanaged => _world.Get<T>(e);

    public bool TryGet<T>(Entity e, out T value) where T : unmanaged => _world.TryGet(e, out value);

    /// <summary>Значение ресурса — копия чистых данных: ресурс со ссылкой внутри не отдаётся, иначе через копию можно
    /// было бы изменить симуляцию.</summary>
    public T Resource<T>() where T : unmanaged => _world.Resource<T>();

    /// <summary>Вид, зарегистрированный владельцем мира (<see cref="WorldBuilder.ReadView{TView}"/>); нет — исключение.</summary>
    public TView View<TView>() where TView : class, IReadView =>
        _world.ViewOf(typeof(TView)) as TView
        ?? throw new InvalidOperationException($"вид {typeof(TView).FullName} не зарегистрирован (WorldBuilder.ReadView)");

    /// <summary>Вид только для чтения, если он зарегистрирован (его регистрирует модуль — без модуля вида нет).</summary>
    public bool TryView<TView>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TView? view) where TView : class, IReadView
    {
        view = _world.ViewOf(typeof(TView)) as TView;
        return view is not null;
    }

    /// <summary>Новый запрос на чтение.</summary>
    public ReadQueryBuilder Query() => new(_world.Query());

    /// <summary>Обход чанков запроса без выделений памяти.</summary>
    public ReadQueryIterator Iterate(ReadQuery query) => new(_world.Iterate(query.Query));
}

/// <summary>Построитель запроса на чтение: <c>Read/With/Without</c>.</summary>
public readonly struct ReadQueryBuilder
{
    private readonly QueryBuilder _builder;

    internal ReadQueryBuilder(QueryBuilder builder) => _builder = builder;

    public ReadQueryBuilder Read<T>() where T : unmanaged => new(_builder.Read<T>());

    public ReadQueryBuilder With<T>() where T : unmanaged => new(_builder.With<T>());

    public ReadQueryBuilder Without<T>() where T : unmanaged => new(_builder.Without<T>());

    public ReadQuery Build() => new(_builder.Build());

    public static implicit operator ReadQuery(ReadQueryBuilder b) => b.Build();
}

/// <summary>Запрос на чтение: подходящие архетипы кешируются, как у <see cref="Ecs.Query"/>.</summary>
public sealed class ReadQuery
{
    internal readonly Query Query;

    internal ReadQuery(Query query) => Query = query;

    /// <summary>Сущностей под запросом сейчас.</summary>
    public int Count() => Query.Count();
}

/// <summary>Обход чанков запроса на чтение: <c>foreach (var chunk in reader.Iterate(q))</c>.</summary>
public struct ReadQueryIterator
{
    private QueryIterator _inner;

    internal ReadQueryIterator(QueryIterator inner) => _inner = inner;

    public readonly ReadQueryIterator GetEnumerator() => this;

    public bool MoveNext() => _inner.MoveNext();

    public readonly ReadChunkView Current => new(_inner.Current);
}

/// <summary>Чанк в обходе на чтение: сущности строк и колонки компонентов только для чтения.</summary>
public readonly struct ReadChunkView
{
    private readonly ChunkView _chunk;

    internal ReadChunkView(ChunkView chunk) => _chunk = chunk;

    public int Count => _chunk.Count;

    public ReadOnlySpan<Entity> Entities => _chunk.Entities;

    public ReadOnlySpan<T> Read<T>() where T : unmanaged => _chunk.Read<T>();
}
