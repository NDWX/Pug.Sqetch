declare @resource nvarchar(255) = N'sqetch:' + @project;
declare @result int;
exec @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
select case when @result >= 0 then 1 when @result = -1 then 0 end;
