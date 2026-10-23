using System;
using System.IO;
using System.Linq;
using LiteDB;
using UMango.Core.Database;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 数据库服务与实体基类的测试。
///
/// 本组的用例全部落在一个临时目录下的 .db 文件上，
/// 验证的是「连接按路径复用」「实体增删改查能穿透到文件」「释放后不留尾巴」，
/// 不涉及 LiteDB 自身的内部实现。
/// </summary>
[Collection("静态状态")]
public class DatabaseServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public DatabaseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "umango-dbtest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void 同一路径两次索取返回同一连接()
    {
        using var service = new DatabaseService();
        var a = service.GetConnection(_dbPath);
        var b = service.GetConnection(_dbPath);
        Assert.Same(a, b);
        Assert.Equal(1, service.ConnectionCount);
    }

    [Fact]
    public void 不同路径返回不同连接()
    {
        using var service = new DatabaseService();
        var a = service.GetConnection(_dbPath);
        var b = service.GetConnection(Path.Combine(_dir, "other.db"));
        Assert.NotSame(a, b);
        Assert.Equal(2, service.ConnectionCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空路径会被拒绝(string? path)
    {
        using var service = new DatabaseService();
        Assert.Throws<ArgumentException>(() => service.GetConnection(path!));
    }

    [Fact]
    public void 路径大小写不同视为不同库()
    {
        // 连接路径按序数比较：大小写不同就是两个不同的库，
        // 静默合并会让人以为"改了设置却没生效"。
        using var service = new DatabaseService();
        var a = service.GetConnection(_dbPath);
        var b = service.GetConnection(_dbPath.ToUpperInvariant());
        Assert.NotSame(a, b);
    }

    [Fact]
    public void 释放后连接数归零且不再接受新连接()
    {
        var service = new DatabaseService();
        service.GetConnection(_dbPath);
        Assert.Equal(1, service.ConnectionCount);

        service.Dispose();
        Assert.Equal(0, service.ConnectionCount);
        Assert.Throws<ObjectDisposedException>(() => service.GetConnection(_dbPath));
    }

    [Fact]
    public void 重复释放不抛异常()
    {
        var service = new DatabaseService();
        service.Dispose();
        service.Dispose(); // 幂等
    }

    /// <summary>
    /// 实体基类：走一遍完整 CRUD，并确认数据真的落到了文件里
    /// （不是只活在一个连接对象的内存里）。
    /// </summary>
    [Fact]
    public void 实体基类可完成增删改查()
    {
        using var service = new DatabaseService();
        using (var entry = new PeopleDatabase(service, _dbPath))
        {
            entry.Insert(new Person { Name = "龙腾猫跃", Age = 10 });
            entry.Insert(new Person { Name = "第二个人", Age = 20 });

            var all = entry.GetAll().ToList();
            Assert.Equal(2, all.Count);

            entry.UpdateAge("第二个人", 21);
            Assert.Equal(21, entry.GetAll().First(p => p.Name == "第二个人").Age);

            entry.Delete("龙腾猫跃");
            Assert.Single(entry.GetAll());
        }

        // 换一个连接对象重新打开同一个文件，数据应仍在 —— 证明落盘了
        using var verify = new DatabaseService();
        using var reopened = new PeopleDatabase(verify, _dbPath);
        var only = Assert.Single(reopened.GetAll());
        Assert.Equal("第二个人", only.Name);
    }

    /// <summary>
    /// 释放服务后，之前发出去的连接**也**必须是关闭状态 ——
    /// 否则文件句柄会一直留着，退出时日志目录还删不掉。
    /// （LiteDB 对已释放连接的任何操作都会抛 LiteException，这里验证的就是这个行为。）
    /// </summary>
    [Fact]
    public void 释放服务会让已发出的连接一并失效()
    {
        var service = new DatabaseService();
        var held = service.GetConnection(_dbPath);
        service.Dispose();

        Assert.Throws<LiteException>(() => held.GetCollection("x").Insert(new BsonDocument { ["a"] = 1 }));
    }

    /// <summary>
    /// 子类的释放逻辑（派生资源）只应跑一次 —— 重复释放不该叠加副作用。
    /// </summary>
    [Fact]
    public void 派生释放逻辑只执行一次()
    {
        using var service = new DatabaseService();
        var entry = new CountingDatabase(service, _dbPath);
        entry.Dispose();
        entry.Dispose();
        entry.Dispose();
        Assert.Equal(1, entry.DisposeCalls);
    }

    [Fact]
    public void 实体基类重复释放不抛异常()
    {
        using var service = new DatabaseService();
        var entry = new PeopleDatabase(service, _dbPath);
        entry.Dispose();
        entry.Dispose();
    }

    [Fact]
    public void 直接传入连接的实体也能工作()
    {
        using var service = new DatabaseService();
        using var db = service.GetConnection(_dbPath);
        using var entry = new PeopleDatabase(db);
        entry.Insert(new Person { Name = "直连", Age = 1 });
        Assert.Single(entry.GetAll());
    }

    /// <summary>计数型实体：用来验证 Dispose 的幂等性真的传导到了子类。</summary>
    private sealed class CountingDatabase : DatabaseEntry
    {
        public int DisposeCalls { get; private set; }

        public CountingDatabase(DatabaseService service, string path) : base(service, path)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) DisposeCalls++;
        }
    }

    private sealed class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    /// <summary>演示用实体：按 PCL 的 README 建议模式实现。</summary>
    private sealed class PeopleDatabase : DatabaseEntry
    {
        private readonly ILiteCollection<Person> _people;

        public PeopleDatabase(DatabaseService service, string path) : base(service, path)
        {
            _people = Db.GetCollection<Person>("people");
            _people.EnsureIndex(p => p.Name);
        }

        public PeopleDatabase(LiteDatabase database) : base(database)
        {
            _people = database.GetCollection<Person>("people");
            _people.EnsureIndex(p => p.Name);
        }

        // LiteDB 5 的枚举入口是 FindAll（All() 是 v4 的 API，v5 已移除）
        public System.Collections.Generic.IEnumerable<Person> GetAll() => _people.FindAll();

        public void Insert(Person p) => _people.Insert(p);

        public void UpdateAge(string name, int age)
        {
            var found = _people.FindOne(p => p.Name == name);
            if (found is null) return;
            found.Age = age;
            _people.Update(found);
        }

        public void Delete(string name)
        {
            var found = _people.FindOne(p => p.Name == name);
            if (found is not null) _people.Delete(found.Id);
        }
    }
}