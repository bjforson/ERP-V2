using Microsoft.EntityFrameworkCore.Storage;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly NickHRDbContext _context;
    private IDbContextTransaction? _transaction;

    public UnitOfWork(NickHRDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public async Task BeginTransactionAsync()
    {
        _transaction = await _context.Database.BeginTransactionAsync();
    }

    public async Task CommitAsync()
    {
        if (_transaction != null)
        {
            await _transaction.CommitAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackAsync()
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
    }
}
