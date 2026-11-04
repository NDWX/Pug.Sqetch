select release_lock( concat( 'sqetch:', sha1( concat( database(), ':', @project ) ) ) )
