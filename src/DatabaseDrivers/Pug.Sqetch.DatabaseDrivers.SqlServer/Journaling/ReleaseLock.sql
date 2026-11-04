declare @resource nvarchar(255) = N'sqetch:' + @project;
exec sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
